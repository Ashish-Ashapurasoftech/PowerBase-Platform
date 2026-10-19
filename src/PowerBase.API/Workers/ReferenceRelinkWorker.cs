using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;
using PowerBase.Infrastructure.Repositories;
using PowerBase.Infrastructure.Services;

namespace PowerBase.API.Workers;

/// <summary>Runs queued re-link jobs (see <see cref="IReferenceRelinkService"/>) in the background, so saving a parent record never
/// waits on its child table. Jobs live in the control DB, so a restart loses none; a job whose worker died is picked up again once its
/// lease expires, and running a job twice is harmless (a row already linked is never touched again).</summary>
public sealed class ReferenceRelinkWorker(IServiceProvider services, ILogger<ReferenceRelinkWorker> logger) : BackgroundService
{
    private const int BatchSize = 10;
    private const int Concurrency = 2;
    private const int LeaseSeconds = 600;
    private const int MaxAttempts = 5;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly string _workerId = $"relink_worker_{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReferenceRelinkWakeNotifier.WaitAsync(PollInterval, stoppingToken);

                IReadOnlyList<ReferenceRelinkJob> jobs;
                using (var scope = services.CreateScope())
                    jobs = await scope.ServiceProvider.GetRequiredService<IReferenceRelinkQueue>()
                        .ClaimAsync(_workerId, BatchSize, LeaseSeconds, stoppingToken);
                if (jobs.Count == 0) continue;

                await Parallel.ForEachAsync(jobs,
                    new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = stoppingToken },
                    async (job, ct) => await RunAsync(job, ct));
                if (jobs.Count == BatchSize) ReferenceRelinkWakeNotifier.Wake(); // a full batch: more may be waiting
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Reference relink worker loop failed.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task RunAsync(ReferenceRelinkJob job, CancellationToken stopping)
    {
        string? error = null;
        string? skipReason = null;
        try
        {
            using var scope = services.CreateScope();
            var sp = scope.ServiceProvider;
            var context = (QueryContext)sp.GetRequiredService<IQueryContext>();
            context.SetTenantId(job.TenantId);

            // The linked rows are written as the user whose save queued the job (ModifiedBy, audit). A deleted user is not found.
            Domain.Entities.User? user = null;
            try { user = await sp.GetRequiredService<IUserRepository>().GetByIdAsync(job.RequestedBy, stopping); }
            catch (NotFoundException) { }

            if (user is null)
            {
                skipReason = $"The user who queued this job ({job.RequestedBy}) no longer exists.";
            }
            else
            {
                context.SetUserIdentity(user.Id, user.SystemRoleCode == SystemRoleCodes.SuperAdmin, user.Name, user.Email,
                    new HashSet<string>(), string.Empty);
                var ids = JsonSerializer.Deserialize<List<long>>(job.ParentRecordIds) ?? [];
                var linked = await sp.GetRequiredService<IReferenceRelinkService>().RunAsync(job.ParentTableId, ids, stopping);
                if (linked > 0)
                    logger.LogInformation("Reference relink job {Id} linked {Count} child record(s) (tenant {TenantId}, table {TableId}).",
                        job.Id, linked, job.TenantId, job.ParentTableId);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            return; // shutting down: the lease expires and the job runs again later
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Reference relink job {Id} failed (attempt {Attempt} of {Max}).", job.Id, job.AttemptCount, MaxAttempts);
            error = ex.Message;
        }

        try
        {
            using var scope = services.CreateScope();
            var queue = scope.ServiceProvider.GetRequiredService<IReferenceRelinkQueue>();
            if (skipReason is not null)
            {
                logger.LogWarning("Reference relink job {Id} skipped: {Reason}", job.Id, skipReason);
                await queue.SkipAsync(job.Id, job.ClaimToken, skipReason, CancellationToken.None);
            }
            else if (error is not null)
            {
                await queue.FailAsync(job.Id, job.ClaimToken, error, retry: job.AttemptCount < MaxAttempts, CancellationToken.None);
            }
            else
            {
                await queue.CompleteAsync(job.Id, job.ClaimToken, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not finalize reference relink job {Id}.", job.Id);
        }
    }
}
