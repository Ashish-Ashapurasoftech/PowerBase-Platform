using System.Collections.Concurrent;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports.DeleteJobs;
using PowerBase.Domain.Constants;
using PowerBase.Infrastructure.Services;

namespace PowerBase.API.Workers;

/// <summary>Polls meta.RecordDeleteJob for Pending jobs (and InProgress jobs whose lease expired after a crash or
/// restart), deletes their records chunk by chunk, saving progress after every chunk, and marks each job
/// Completed when no matching record is left. Failed / Cancelled jobs keep the records already deleted.</summary>
public sealed class RecordDeleteJobWorker(IServiceProvider services, ILogger<RecordDeleteJobWorker> logger) : BackgroundService
{
    private const int GlobalLimit = 3;
    private const int LeaseSeconds = 120;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly string _workerId = $"record_delete_worker_{Guid.NewGuid():N}";
    private readonly SemaphoreSlim _slots = new(GlobalLimit, GlobalLimit);
    private readonly ConcurrentDictionary<long, Task> _active = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RecordDeleteJobWakeNotifier.WaitAsync(PollInterval, stoppingToken);
                var free = _slots.CurrentCount;
                if (free == 0) continue;

                using var scope = services.CreateScope();
                var claimed = await scope.ServiceProvider.GetRequiredService<IRecordDeleteJobRepository>()
                    .ClaimAsync(_workerId, free, LeaseSeconds, stoppingToken);
                foreach (var job in claimed)
                {
                    await _slots.WaitAsync(stoppingToken);
                    _active[job.Id] = RunAsync(job, stoppingToken);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Record delete worker loop failed.");
                try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
        await Task.WhenAny(Task.WhenAll(_active.Values), Task.Delay(TimeSpan.FromSeconds(15)));
    }

    private async Task RunAsync(RecordDeleteJob job, CancellationToken stopping)
    {
        var claimToken = job.ClaimToken ?? Guid.Empty;
        var deleted = job.DeletedCount;
        var status = RecordDeleteJobStatus.Completed;
        string? error = null;
        var leaseLost = false;
        try
        {
            Guid? previousFirstId = null;
            RecordDeleteFastPlan? fastPlan = null;
            var planResolved = false;
            long lastId = 0;
            var cancelRequested = job.CancelRequested;
            while (!stopping.IsCancellationRequested)
            {
                if (cancelRequested) { status = RecordDeleteJobStatus.Cancelled; break; }
                if (deleted >= job.ExpectedCount) break;

                // A fresh scope per chunk = a fresh unit of work, so each chunk is its own short transaction.
                using var scope = services.CreateScope();
                var sp = scope.ServiceProvider;
                if (!await ApplyIdentityAsync(sp, job, stopping))
                {
                    status = RecordDeleteJobStatus.Failed;
                    error = "The user who started this delete is no longer active.";
                    break;
                }

                var processor = sp.GetRequiredService<RecordDeleteJobProcessor>();
                if (!planResolved)
                {
                    // Once per job: a report answerable by plain SQL is walked by Id in big chunks; anything else
                    // (computed/encrypted filters, quick search) keeps the page-by-page report run.
                    fastPlan = await processor.PrepareFastPlanAsync(job, stopping);
                    planResolved = true;
                }

                if (fastPlan is not null)
                {
                    var fast = await processor.DeleteNextFastChunkAsync(fastPlan, lastId, job.ExpectedCount - deleted, stopping);
                    if (fast.Deleted == 0) break; // nothing matches any more → completed
                    lastId = fast.LastId;
                    deleted += fast.Deleted;
                }
                else
                {
                    var chunk = await processor.DeleteNextChunkAsync(job, job.ExpectedCount - deleted, stopping);
                    if (chunk.Deleted == 0) break; // nothing matches any more → completed

                    if (chunk.FirstId is not null && chunk.FirstId == previousFirstId)
                        throw new InvalidOperationException("Deleting made no progress (the same records matched again). Stopped to avoid looping.");
                    previousFirstId = chunk.FirstId;
                    deleted += chunk.Deleted;
                }

                var progress = await sp.GetRequiredService<IRecordDeleteJobRepository>()
                    .ReportProgressAsync(job.Id, _workerId, claimToken, deleted, LeaseSeconds, stopping);
                if (!progress.OwnsLease) { leaseLost = true; break; }
                cancelRequested = progress.CancelRequested;
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { leaseLost = true; } // shutdown: lease expires, job resumes later
        catch (Exception ex)
        {
            logger.LogError(ex, "Record delete job {JobId} failed.", job.PublicId);
            status = RecordDeleteJobStatus.Failed;
            error = ex.Message.Length > 900 ? ex.Message[..900] : ex.Message;
        }

        try
        {
            if (!leaseLost)
            {
                if (deleted > job.DeletedCount && await ApplyAuditAsync(job, deleted, status))
                    logger.LogInformation("Record delete job {JobId} {Status}: {Deleted} record(s).", job.PublicId, status, deleted);

                using var scope = services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IRecordDeleteJobRepository>()
                    .CompleteAsync(job.Id, _workerId, claimToken, status, deleted, error, CancellationToken.None);
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Could not finalize record delete job {JobId}.", job.PublicId); }
        finally
        {
            _slots.Release();
            _active.TryRemove(job.Id, out _);
            RecordDeleteJobWakeNotifier.Wake();
        }
    }

    /// <summary>Runs the job as the user who requested it, so permission checks behave as they would in the UI.</summary>
    private static async Task<bool> ApplyIdentityAsync(IServiceProvider sp, RecordDeleteJob job, CancellationToken ct)
    {
        var context = (QueryContext)sp.GetRequiredService<IQueryContext>();
        context.SetTenantId(job.TenantId);
        var user = await sp.GetRequiredService<IUserRepository>().GetByIdAsync(job.RequestedByUserId, ct);
        if (!user.IsActive || user.IsDeleted) return false;
        context.SetUserIdentity(user.Id, job.IsSuperAdmin, user.Name, user.Email, new HashSet<string>(), job.TenantRole);
        return true;
    }

    private async Task<bool> ApplyAuditAsync(RecordDeleteJob job, int deleted, string status)
    {
        try
        {
            using var scope = services.CreateScope();
            if (!await ApplyIdentityAsync(scope.ServiceProvider, job, CancellationToken.None)) return false;
            await scope.ServiceProvider.GetRequiredService<RecordDeleteJobProcessor>()
                .LogSummaryAsync(job, deleted, status, CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not write the audit entry for record delete job {JobId}.", job.PublicId);
            return false;
        }
    }
}
