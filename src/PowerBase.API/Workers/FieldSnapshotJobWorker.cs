using System.Collections.Concurrent;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.SnapshotJobs;
using PowerBase.Infrastructure.Services;

namespace PowerBase.API.Workers;

/// <summary>Polls meta.FieldSnapshotJob for Pending jobs (and InProgress jobs whose lease expired after a crash or
/// restart) and re-captures the Snapshot field's source value for the table's records chunk by chunk, saving progress
/// and the resume cursor after every chunk. A job is marked Completed when no record is left after the cursor;
/// Failed / Cancelled jobs keep the records already processed.</summary>
public sealed class FieldSnapshotJobWorker(IServiceProvider services, ILogger<FieldSnapshotJobWorker> logger) : BackgroundService
{
    private const int GlobalLimit = 2;
    private const int LeaseSeconds = 120;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly string _workerId = $"field_snapshot_worker_{Guid.NewGuid():N}";
    private readonly SemaphoreSlim _slots = new(GlobalLimit, GlobalLimit);
    private readonly ConcurrentDictionary<long, Task> _active = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await FieldSnapshotJobWakeNotifier.WaitAsync(PollInterval, stoppingToken);
                var free = _slots.CurrentCount;
                if (free == 0) continue;

                using var scope = services.CreateScope();
                var claimed = await scope.ServiceProvider.GetRequiredService<IFieldSnapshotJobRepository>()
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
                logger.LogError(ex, "Field snapshot worker loop failed.");
                try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
        await Task.WhenAny(Task.WhenAll(_active.Values), Task.Delay(TimeSpan.FromSeconds(15)));
    }

    private async Task RunAsync(FieldSnapshotJob job, CancellationToken stopping)
    {
        var claimToken = job.ClaimToken ?? Guid.Empty;
        var processed = job.ProcessedCount;
        var changed = job.ChangedCount;
        var lastId = job.LastRecordId;
        var status = FieldSnapshotJobStatus.Completed;
        string? error = null;
        var leaseLost = false;
        try
        {
            var cancelRequested = job.CancelRequested;
            while (!stopping.IsCancellationRequested)
            {
                if (cancelRequested) { status = FieldSnapshotJobStatus.Cancelled; break; }

                // A fresh scope per chunk = a fresh unit of work, so each chunk is its own short transaction.
                using var scope = services.CreateScope();
                var sp = scope.ServiceProvider;
                if (!await ApplyIdentityAsync(sp, job, stopping))
                {
                    status = FieldSnapshotJobStatus.Failed;
                    error = "The user who started this Reinitialize is no longer active.";
                    break;
                }

                var chunk = await sp.GetRequiredService<FieldSnapshotJobProcessor>().ProcessNextChunkAsync(job, lastId, stopping);
                if (chunk.Processed == 0) break; // no record left after the cursor → completed
                lastId = chunk.LastId;
                processed += chunk.Processed;
                changed += chunk.Changed;

                var progress = await sp.GetRequiredService<IFieldSnapshotJobRepository>()
                    .ReportProgressAsync(job.Id, _workerId, claimToken, processed, changed, lastId, LeaseSeconds, stopping);
                if (!progress.OwnsLease) { leaseLost = true; break; }
                cancelRequested = progress.CancelRequested;
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { leaseLost = true; } // shutdown: lease expires, job resumes later
        catch (Exception ex)
        {
            logger.LogError(ex, "Field snapshot job {JobId} failed.", job.PublicId);
            status = FieldSnapshotJobStatus.Failed;
            error = ex.Message.Length > 900 ? ex.Message[..900] : ex.Message;
        }

        try
        {
            if (!leaseLost)
            {
                if (await ApplyAuditAsync(job, processed, changed, status))
                    logger.LogInformation("Field snapshot job {JobId} {Status}: {Processed} record(s) read, {Changed} changed.", job.PublicId, status, processed, changed);

                using var scope = services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IFieldSnapshotJobRepository>()
                    .CompleteAsync(job.Id, _workerId, claimToken, status, processed, changed, error, CancellationToken.None);
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Could not finalize field snapshot job {JobId}.", job.PublicId); }
        finally
        {
            _slots.Release();
            _active.TryRemove(job.Id, out _);
            FieldSnapshotJobWakeNotifier.Wake();
        }
    }

    /// <summary>Runs the job as the user who requested it, so permission checks behave as they would in the UI.</summary>
    private static async Task<bool> ApplyIdentityAsync(IServiceProvider sp, FieldSnapshotJob job, CancellationToken ct)
    {
        var context = (QueryContext)sp.GetRequiredService<IQueryContext>();
        context.SetTenantId(job.TenantId);
        var user = await sp.GetRequiredService<IUserRepository>().GetByIdAsync(job.RequestedByUserId, ct);
        if (!user.IsActive || user.IsDeleted) return false;
        context.SetUserIdentity(user.Id, job.IsSuperAdmin, user.Name, user.Email, new HashSet<string>(), job.TenantRole);
        return true;
    }

    private async Task<bool> ApplyAuditAsync(FieldSnapshotJob job, int processed, int changed, string status)
    {
        try
        {
            using var scope = services.CreateScope();
            if (!await ApplyIdentityAsync(scope.ServiceProvider, job, CancellationToken.None)) return false;
            await scope.ServiceProvider.GetRequiredService<FieldSnapshotJobProcessor>()
                .LogSummaryAsync(job, processed, changed, status, CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not write the audit entry for field snapshot job {JobId}.", job.PublicId);
            return false;
        }
    }
}
