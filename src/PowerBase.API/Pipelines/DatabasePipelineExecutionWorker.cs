using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Pipelines;
using PowerBase.Infrastructure.Services;

namespace PowerBase.API.Pipelines;

public class DatabasePipelineExecutionWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IControlConnectionFactory _controlConnFactory;
    private readonly PipelineExecutionOptions _options;
    private readonly ILogger<DatabasePipelineExecutionWorker> _logger;
    private readonly string _workerId;
    
    private readonly ConcurrentDictionary<string, Task> _activeTasks = new();
    private static readonly ConcurrentDictionary<long, SemaphoreSlim> TenantSemaphores = new();

    public DatabasePipelineExecutionWorker(
        IServiceProvider serviceProvider,
        IControlConnectionFactory controlConnFactory,
        IOptions<PipelineExecutionOptions> options,
        ILogger<DatabasePipelineExecutionWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _controlConnFactory = controlConnFactory;
        _options = options.Value;
        _logger = logger;
        _workerId = $"db_worker_{Guid.NewGuid()}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Database Pipeline Execution Worker started. Worker ID: {WorkerId}", _workerId);

        // Run maintenance sweep on startup to clear any stuck pending items exceeding MaxAttempts
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var queueRepo = scope.ServiceProvider.GetRequiredService<IMainPipelineQueueRepository>();
            var swept = await queueRepo.SweepExhaustedPendingJobsAsync(stoppingToken);
            if (swept > 0)
            {
                _logger.LogInformation("Startup maintenance swept {Count} exhausted pending items to Failed.", swept);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run startup maintenance sweep.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Await wake signal or timeout
                await Task.WhenAny(
                    DatabasePipelineQueueWakeNotifier.WaitForJobAsync(stoppingToken),
                    Task.Delay(TimeSpan.FromSeconds(_options.DatabaseQueue.QueuePollingIntervalSeconds), stoppingToken)
                );

                if (stoppingToken.IsCancellationRequested) break;

                // 1. Query eligible tenants based on process-local semaphore limit
                var eligibleTenants = await GetEligibleTenantsAsync(stoppingToken);

                // 2. Reclaim expired Processing leases directly
                await ReclaimExpiredLeasesAsync(eligibleTenants, stoppingToken);

                if (eligibleTenants.Count == 0)
                {
                    continue;
                }

                // 3. Claim new Pending jobs
                IReadOnlyList<PipelineQueue> claimed;
                using (var scope = _serviceProvider.CreateScope())
                {
                    var queueRepo = scope.ServiceProvider.GetRequiredService<IMainPipelineQueueRepository>();
                    claimed = await queueRepo.ClaimPendingJobsAsync(
                        _workerId,
                        _options.DatabaseQueue.ExecutionBatchSize,
                        _options.DatabaseQueue.LeaseSeconds,
                        eligibleTenants,
                        stoppingToken);
                }

                foreach (var job in claimed)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    
                    // Dispatch execution in separate background task
                    var taskKey = job.PublicId.ToString();
                    var tcs = new TaskCompletionSource<bool>();
                    _activeTasks[taskKey] = tcs.Task;

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await ProcessJobAsync(job, stoppingToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Fatal execution error for job {Id} (MessageId: {MessageId}).", job.Id, job.MessageId);
                        }
                        finally
                        {
                            _activeTasks.TryRemove(taskKey, out _);
                            tcs.SetResult(true);
                        }
                    }, stoppingToken);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred in Database Worker main loop.");
                await Task.Delay(2000, stoppingToken);
            }
        }

        // Graceful shutdown
        if (!_activeTasks.IsEmpty)
        {
            _logger.LogInformation("Waiting for {ActiveTaskCount} active database tasks to finish...", _activeTasks.Count);
            await Task.WhenAny(Task.WhenAll(_activeTasks.Values), Task.Delay(TimeSpan.FromSeconds(_options.WorkerShutdownWaitSeconds)));
        }
    }

    private async Task ReclaimExpiredLeasesAsync(List<long> eligibleTenantIds, CancellationToken ct)
    {
        try
        {
            IReadOnlyList<PipelineQueue> reclaimed;
            using (var scope = _serviceProvider.CreateScope())
            {
                var queueRepo = scope.ServiceProvider.GetRequiredService<IMainPipelineQueueRepository>();
                reclaimed = await queueRepo.ReclaimExpiredJobsAsync(
                    _workerId,
                    _options.DatabaseQueue.ExecutionBatchSize,
                    _options.DatabaseQueue.LeaseSeconds,
                    eligibleTenantIds,
                    ct);
            }

            foreach (var job in reclaimed)
            {
                if (job.Status == "Failed")
                {
                    _logger.LogWarning("Reclaimed job {Id} (MessageId: {MessageId}) exceeded max attempts and is now Failed.", job.Id, job.MessageId);
                    continue;
                }

                var taskKey = job.PublicId.ToString();
                var tcs = new TaskCompletionSource<bool>();
                _activeTasks[taskKey] = tcs.Task;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ProcessJobAsync(job, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Fatal error running reclaimed job {Id}.", job.Id);
                    }
                    finally
                    {
                        _activeTasks.TryRemove(taskKey, out _);
                        tcs.SetResult(true);
                    }
                }, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed during expired job reclamation run.");
        }
    }

    private async Task<List<long>> GetEligibleTenantsAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = _controlConnFactory.Create();
            await conn.OpenAsync(ct);
            var query = await conn.QueryAsync<long>(
                new CommandDefinition(
                    "SELECT Id FROM meta.Tenant WHERE IsDeleted = 0 AND ProvisioningState = 'Ready'",
                    cancellationToken: ct));

            return query
                .Where(tId =>
                {
                    var sem = TenantSemaphores.GetOrAdd(tId, _ => new SemaphoreSlim(_options.PerInstanceTenantConcurrencyLimit, _options.PerInstanceTenantConcurrencyLimit));
                    return sem.CurrentCount > 0;
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build eligible tenant capacity list.");
            return [];
        }
    }

    private async Task ProcessJobAsync(PipelineQueue job, CancellationToken ct)
    {
        var semaphore = TenantSemaphores.GetOrAdd(job.TenantId, _ => new SemaphoreSlim(_options.PerInstanceTenantConcurrencyLimit, _options.PerInstanceTenantConcurrencyLimit));
        await semaphore.WaitAsync(ct);

        CancellationTokenSource heartbeatCts = new();
        Task? heartbeatTask = null;

        try
        {
            // Resolve ClaimToken and status context
            var claimToken = job.ClaimToken ?? Guid.Empty;
            if (claimToken == Guid.Empty)
            {
                throw new InvalidOperationException($"Job {job.Id} claimed with invalid token.");
            }

            // Start Main DB Queue Lease heartbeat loop
            heartbeatTask = Task.Run(async () =>
            {
                while (!heartbeatCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(_options.DatabaseQueue.HeartbeatSeconds), heartbeatCts.Token);
                        if (heartbeatCts.Token.IsCancellationRequested) break;

                        using var scope = _serviceProvider.CreateScope();
                        var queueRepo = scope.ServiceProvider.GetRequiredService<IMainPipelineQueueRepository>();
                        var renewed = await queueRepo.RenewLeaseAsync(job.Id, _workerId, claimToken, _options.DatabaseQueue.LeaseSeconds, heartbeatCts.Token);
                        if (!renewed)
                        {
                            _logger.LogWarning("Worker lease renewal failed for Job {Id} (MessageId: {MessageId}). Ownership may have been lost.", job.Id, job.MessageId);
                            break;
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error in lease heartbeat loop for Job {Id}.", job.Id);
                    }
                }
            }, heartbeatCts.Token);

            // Execute execution within a scoped context
            using var scope = _serviceProvider.CreateScope();
            
            var queryContext = scope.ServiceProvider.GetRequiredService<IQueryContext>();
            queryContext.SetTenantId(job.TenantId);
            if (queryContext is QueryContext qc)
            {
                qc.UserId = job.TriggeredBy ?? 0;
                qc.IsPipelineExecution = true;
                qc.PipelineDepth = job.Depth;
                qc.PipelineChainJson = job.PipelineChain;
            }

            var pipelineRepo = scope.ServiceProvider.GetRequiredService<IPipelineRepository>();
            var queueRepo = scope.ServiceProvider.GetRequiredService<IMainPipelineQueueRepository>();

            // 1. Tenant PipelineRun Reconciliation & Idempotency Check
            var run = await pipelineRepo.GetRunByMessageIdAsync(job.MessageId, ct);
            if (run != null)
            {
                if (run.Status == "Running")
                {
                    var isLeaseExpired = run.LockedUntil <= DateTime.UtcNow;
                    if (!isLeaseExpired)
                    {
                        // Job is active in another worker thread, yield and reschedule
                        _logger.LogWarning("Reconciliation: PipelineRun {RunId} is actively Running in Tenant DB. Yielding job {Id}.", run.Id, job.Id);
                        await queueRepo.ScheduleRetryAsync(job.Id, _workerId, claimToken, 10, "Yielded: Task running in Tenant DB.", ct);
                        return;
                    }
                    
                    // Lease expired: Reclaim stale run
                    var reclaimed = await pipelineRepo.ReclaimStaleRunAsync(job.MessageId, _workerId, ct);
                    if (!reclaimed)
                    {
                        await queueRepo.ScheduleRetryAsync(job.Id, _workerId, claimToken, 10, "Failed to reclaim stale Tenant run lease.", ct);
                        return;
                    }
                }
                else if (run.Status == "Success")
                {
                    _logger.LogInformation("Reconciliation: PipelineRun {RunId} already succeeded. Marking Succeeded in Main DB.", run.Id);
                    await queueRepo.MarkSucceededAsync(job.Id, _workerId, claimToken, ct);
                    return;
                }
                else if (run.Status == "Skipped" || run.Status == "Stopped")
                {
                    _logger.LogInformation("Reconciliation: PipelineRun {RunId} has terminal status {Status}. Syncing Main DB.", run.Id, run.Status);
                    await queueRepo.MarkSkippedAsync(job.Id, _workerId, claimToken, run.Status == "Skipped" ? "Skipped in Tenant DB" : "Stopped in Tenant DB", ct);
                    return;
                }
                else if (run.Status == "Failed")
                {
                    // If attempts exhausted, fail Main DB. Otherwise retry.
                    if (job.AttemptCount >= job.MaxAttempts)
                    {
                        _logger.LogError("Reconciliation: Tenant run failed and attempts exhausted for Job {Id}.", job.Id);
                        await queueRepo.MarkFailedAsync(job.Id, _workerId, claimToken, run.LastError ?? "Tenant run failed.", ct);
                        return;
                    }

                    // Reclaim for retry execution
                    var reclaimed = await pipelineRepo.ClaimFailedRunRetryAsync(job.MessageId, _workerId, ct);
                    if (!reclaimed)
                    {
                        await queueRepo.ScheduleRetryAsync(job.Id, _workerId, claimToken, 10, "Failed to reclaim failed Tenant run lease.", ct);
                        return;
                    }
                }
            }

            // 2. Start Execution
            var task = new PipelineExecutionTask
            {
                TenantId = job.TenantId,
                PipelineId = job.PipelineId,
                TriggerEvent = job.TriggerEvent ?? "manual",
                TriggerPayloadJson = job.TriggerPayloadJson,
                TriggeredBy = job.TriggeredBy ?? 0,
                TriggerTablePublicId = job.TriggerTablePublicId,
                VariablesJson = job.VariablesJson,
                CorrelationId = job.CorrelationId?.ToString(),
                Depth = job.Depth,
                MessageId = job.MessageId.ToString(),
                WorkerId = _workerId
            };

            var engine = scope.ServiceProvider.GetRequiredService<IPipelineEngine>();
            
            try
            {
                await engine.ExecuteAsync(task, ct);

                // Verification of execution status post run
                var finalRun = await pipelineRepo.GetRunByMessageIdAsync(job.MessageId, ct);
                if (finalRun != null && finalRun.Status == "Success")
                {
                    await queueRepo.MarkSucceededAsync(job.Id, _workerId, claimToken, ct);
                }
                else if (finalRun != null && (finalRun.Status == "Skipped" || finalRun.Status == "Stopped"))
                {
                    await queueRepo.MarkSkippedAsync(job.Id, _workerId, claimToken, finalRun.LastError ?? "Skipped", ct);
                }
                else if (finalRun != null && finalRun.Status == "Failed")
                {
                    await HandleJobFailureAsync(queueRepo, job, claimToken, finalRun.LastError ?? "Execution failed.", ct);
                }
                else
                {
                    await queueRepo.MarkSucceededAsync(job.Id, _workerId, claimToken, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pipeline execution threw an exception for Job {Id}.", job.Id);
                await HandleJobFailureAsync(queueRepo, job, claimToken, ex.Message, ct);
            }
        }
        finally
        {
            // Terminate heartbeat loop
            try
            {
                heartbeatCts.Cancel();
                if (heartbeatTask != null)
                {
                    await heartbeatTask;
                }
            }
            catch { }
            finally
            {
                heartbeatCts.Dispose();
            }

            semaphore.Release();
        }
    }

    private async Task HandleJobFailureAsync(IMainPipelineQueueRepository queueRepo, PipelineQueue job, Guid claimToken, string error, CancellationToken ct)
    {
        if (job.AttemptCount >= job.MaxAttempts)
        {
            _logger.LogError("Attempts exhausted for Job {Id} (MessageId: {MessageId}). Marking Failed.", job.Id, job.MessageId);
            await queueRepo.MarkFailedAsync(job.Id, _workerId, claimToken, error, ct);
        }
        else
        {
            var backoff = _options.DatabaseQueue.BaseRetryDelaySeconds * (int)Math.Pow(2, job.AttemptCount);
            _logger.LogInformation("Scheduling backoff retry in {Seconds}s for Job {Id} (MessageId: {MessageId}).", backoff, job.Id, job.MessageId);
            await queueRepo.ScheduleRetryAsync(job.Id, _workerId, claimToken, backoff, error, ct);
        }
    }
}
