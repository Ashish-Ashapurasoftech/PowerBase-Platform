using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NCrontab;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Pipelines;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PowerBase.API.Pipelines;

public class PipelineSchedulerWorker : BackgroundService
{
    private readonly IControlConnectionFactory _controlConnectionFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly Microsoft.Extensions.Options.IOptions<PowerBase.Application.Common.Configurations.PipelineExecutionOptions> _options;
    private readonly ILogger<PipelineSchedulerWorker> _logger;

    public PipelineSchedulerWorker(
        IControlConnectionFactory controlConnectionFactory,
        IServiceProvider serviceProvider,
        Microsoft.Extensions.Options.IOptions<PowerBase.Application.Common.Configurations.PipelineExecutionOptions> options,
        ILogger<PipelineSchedulerWorker> logger)
    {
        _controlConnectionFactory = controlConnectionFactory;
        _serviceProvider = serviceProvider;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Pipeline Scheduler background worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Run checks every 60 seconds
                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
                await EvaluateSchedulesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Pipeline Scheduler background worker shutting down gracefully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Pipeline Scheduler background worker main execution loop.");
            }
        }
    }

    private async Task EvaluateSchedulesAsync(CancellationToken ct)
    {
        // 1. Get all active and ready tenants from the control database
        List<long> tenantIds;
        try
        {
            await using var conn = _controlConnectionFactory.Create();
            await conn.OpenAsync(ct);
            var query = await conn.QueryAsync<long>(
                new CommandDefinition(
                    "SELECT Id FROM meta.Tenant WHERE IsDeleted = 0 AND ProvisioningState = 'Ready'",
                    cancellationToken: ct));
            tenantIds = query.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query active tenants from control database.");
            return;
        }

        // Bounded Outstanding-Work Discovery (queries Control DB exactly once)
        Dictionary<long, List<long>> outstandingWork = new();
        try
        {
            await using var conn = _controlConnectionFactory.Create();
            await conn.OpenAsync(ct);
            var discoverySql = "SELECT DISTINCT TenantId, PipelineId FROM meta.PipelineQueue WHERE Status = 'Pending';";
            var pendingJobs = await conn.QueryAsync<(long TenantId, long PipelineId)>(
                new CommandDefinition(discoverySql, cancellationToken: ct));

            outstandingWork = pendingJobs
                .GroupBy(j => j.TenantId)
                .ToDictionary(g => g.Key, g => g.Select(j => j.PipelineId).Distinct().ToList());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to discover outstanding queue work for reconciliation.");
        }

        // 2. Evaluate schedules per tenant
        foreach (var tenantId in tenantIds)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var queryContext = scope.ServiceProvider.GetRequiredService<IQueryContext>();
                queryContext.SetTenantId(tenantId);

                // Acquire SQL Session App Lock per tenant
                var connectionFactory = scope.ServiceProvider.GetRequiredService<ITenantConnectionFactory>();
                await using var lockConn = await connectionFactory.CreateAsync(ct);
                await lockConn.OpenAsync(ct);

                const string lockSql = "DECLARE @res INT; EXEC @res = sp_getapplock @Resource = @resourceName, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @res;";
                var resourceName = $"PB_PipelineScheduler_Tenant_{tenantId}";
                var lockResult = await lockConn.QuerySingleOrDefaultAsync<int>(lockSql, new { resourceName });

                if (lockResult < 0)
                {
                    _logger.LogInformation("Tenant {TenantId} schedule evaluation is already locked by another scheduler instance. Skipping.", tenantId);
                    continue;
                }

                try
                {
                    var pipelineRepo = scope.ServiceProvider.GetRequiredService<IPipelineRepository>();
                    var queue = scope.ServiceProvider.GetRequiredService<IPipelineExecutionQueue>();
                    var queueRepo = scope.ServiceProvider.GetRequiredService<IMainPipelineQueueRepository>();

                    var utcNow = DateTime.UtcNow;

                    // Combined metadata query (1 Tenant DB query instead of 3)
                    var metadata = await pipelineRepo.GetSchedulerMetadataAsync(ct);

                    // Bounded Queue Reconciliation Sweep (self-healing recovery)
                    if (outstandingWork.TryGetValue(tenantId, out var pendingPipelineIds) && pendingPipelineIds.Count > 0)
                    {
                        var pipelineStates = await pipelineRepo.GetPipelineStatesAsync(pendingPipelineIds, ct);
                        var activePipelineIds = new List<long>();
                        var deletedPipelineIds = new List<long>();

                        var queriedIds = pipelineStates.Select(p => p.Id).ToHashSet();
                        
                        // Orphaned/missing pipelines that exist in Queue but not in Pipeline table
                        var missingIds = pendingPipelineIds.Where(id => !queriedIds.Contains(id)).ToList();
                        deletedPipelineIds.AddRange(missingIds);

                        foreach (var state in pipelineStates)
                        {
                            if (state.IsDeleted)
                            {
                                deletedPipelineIds.Add(state.Id);
                            }
                            else if (state.IsActive)
                            {
                                activePipelineIds.Add(state.Id);
                            }
                        }

                        var sentinelDate = new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);
                        if (activePipelineIds.Count > 0)
                        {
                            var resumedCount = await queueRepo.ResumePendingJobsForPipelinesAsync(tenantId, activePipelineIds, sentinelDate, ct);
                            if (resumedCount > 0)
                            {
                                _logger.LogInformation("Recovery sweep: Resumed {ResumedCount} sentinel-paused pending jobs for active pipelines in Tenant {TenantId}.", resumedCount, tenantId);
                            }
                        }

                        if (deletedPipelineIds.Count > 0)
                        {
                            var cancelledCount = await queueRepo.CancelPendingJobsForPipelinesAsync(tenantId, deletedPipelineIds, "Pipeline deleted", ct);
                            if (cancelledCount > 0)
                            {
                                _logger.LogInformation("Recovery sweep: Cancelled {CancelledCount} pending jobs for deleted/missing pipelines in Tenant {TenantId}.", cancelledCount, tenantId);
                            }
                        }
                    }

                    // Route 1: Evaluate Existing Schedule Steps (Unchanged canvas-level trigger blocks)
                    foreach (var step in metadata.ActiveScheduleSteps)
                    {
                        if (ct.IsCancellationRequested) break;

                        try
                        {
                            var config = string.IsNullOrEmpty(step.ConfigJson)
                                ? new SchedulerStepConfig()
                                : JsonSerializer.Deserialize<SchedulerStepConfig>(step.ConfigJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new SchedulerStepConfig();

                            if (string.IsNullOrWhiteSpace(config.CronExpression))
                            {
                                continue;
                            }

                            // Resolve timezone info
                            TimeZoneInfo timeZoneInfo = TimeZoneMapper.ResolveTimeZone(config.TimeZone ?? "UTC");

                            // Convert target evaluation window to step local timezone
                            var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(utcNow, timeZoneInfo);
                            var startLocal = step.LastTriggeredOn.HasValue
                                ? TimeZoneInfo.ConvertTimeFromUtc(step.LastTriggeredOn.Value, timeZoneInfo)
                                : nowLocal.AddMinutes(-2);

                            // If last triggered time is in the future relative to window, ignore
                            if (startLocal >= nowLocal)
                            {
                                continue;
                            }

                            var schedule = CrontabSchedule.Parse(config.CronExpression);
                            var occurrences = schedule.GetNextOccurrences(startLocal, nowLocal).ToList();

                            if (occurrences.Count > 0)
                            {
                                var latestOccurrenceLocal = occurrences.Last();
                                var latestOccurrenceUtc = TimeZoneInfo.ConvertTimeToUtc(latestOccurrenceLocal, timeZoneInfo);

                                var pipeline = await pipelineRepo.GetByIdAsync(step.PipelineId, ct);
                                if (pipeline == null || !pipeline.IsActive || pipeline.IsDeleted)
                                {
                                    continue;
                                }

                                // Authoritative current-state check
                                var currentSteps = await pipelineRepo.GetStepsByPipelineIdAsync(step.PipelineId, ct);
                                var activeSteps = currentSteps.Where(s => !s.IsDeleted).ToList();
                                var rootSteps = activeSteps.Where(s => s.ParentStepId == null).ToList();

                                if (rootSteps.Count != 1)
                                {
                                    _logger.LogWarning("Skipping scheduled enqueue for Step {StepId} of Pipeline {PipelineId}: Multiple active root steps found.", step.Id, step.PipelineId);
                                    continue;
                                }

                                var rootStep = rootSteps[0];
                                if (rootStep == null || rootStep.Type != "trigger" || rootStep.Subtype != "schedule" || rootStep.Id != step.Id)
                                {
                                    _logger.LogWarning("Skipping scheduled enqueue for Step {StepId} of Pipeline {PipelineId}: root step trigger mismatch or invalid configuration.", step.Id, step.PipelineId);
                                    continue;
                                }

                                Guid messageId;
                                if (pipeline != null)
                                {
                                    var hashInput = pipeline.PublicId.ToString() + "_" + step.PublicId.ToString() + "_" + latestOccurrenceUtc.ToString("o");
                                    var hashBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(hashInput));
                                    var guidBytes = new byte[16];
                                    Array.Copy(hashBytes, guidBytes, 16);
                                    messageId = new Guid(guidBytes);
                                }
                                else
                                {
                                    messageId = Guid.NewGuid();
                                }

                                var task = new PipelineExecutionTask
                                {
                                    TenantId = tenantId,
                                    PipelineId = step.PipelineId,
                                    TriggerEvent = "schedule",
                                    TriggerPayloadJson = JsonSerializer.Serialize(new
                                    {
                                        TriggerTime = latestOccurrenceUtc,
                                        ScheduledTime = latestOccurrenceLocal,
                                        CronExpression = config.CronExpression,
                                        TriggerStepId = step.Id,
                                        TriggerStepRefId = step.RefId
                                    }),
                                    TriggeredBy = 0,
                                    VariablesJson = null,
                                    CorrelationId = Guid.NewGuid().ToString(),
                                    Depth = 1,
                                    MessageId = messageId.ToString()
                                };

                                bool enqueueSuccess = false;
                                try
                                {
                                    queue.QueueTask(task);
                                    enqueueSuccess = true;
                                }
                                catch (PowerBase.Infrastructure.Pipelines.MessageDeduplicatedException)
                                {
                                    _logger.LogInformation("Schedule occurrences deduplicated and treated as successful enqueue for Step {StepId}.", step.Id);
                                    enqueueSuccess = true;
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "Failed to enqueue scheduled execution for Step {StepId}.", step.Id);
                                }

                                if (enqueueSuccess)
                                {
                                    await pipelineRepo.UpdateStepLastTriggeredOnAsync(step.Id, step.LastTriggeredOn, latestOccurrenceUtc, ct);
                                    _logger.LogInformation("Enqueued scheduled pipeline execution for Step {StepId} (Tenant {TenantId}) at scheduled local time {ScheduledTime}.", step.Id, tenantId, latestOccurrenceLocal);
                                }
                            }
                        }
                        catch (Exception stepEx)
                        {
                            _logger.LogError(stepEx, "Error evaluating schedule step {StepId} for tenant {TenantId}.", step.Id, tenantId);
                        }
                    }

                    // Route 2: Evaluate New Pipeline-Level Schedules
                    foreach (var sched in metadata.ActiveSchedules)
                    {
                        if (ct.IsCancellationRequested) break;

                        try
                        {
                            if (string.IsNullOrWhiteSpace(sched.CronExpression))
                            {
                                continue;
                            }

                            // Resolve timezone info
                            TimeZoneInfo timeZoneInfo = TimeZoneMapper.ResolveTimeZone(sched.TimeZone);

                            // Convert target evaluation window to local timezone
                            var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(utcNow, timeZoneInfo);

                            // Initialize NextRunOn if missing (fresh start or reactivation)
                            if (!sched.NextRunOn.HasValue)
                            {
                                try
                                {
                                    var scheduleInstance = CrontabSchedule.Parse(sched.CronExpression);
                                    var startForNext = sched.LastRunOn.HasValue
                                        ? TimeZoneInfo.ConvertTimeFromUtc(sched.LastRunOn.Value, timeZoneInfo)
                                        : nowLocal.AddMinutes(-1);
                                    var nextOccurrenceLocal = scheduleInstance.GetNextOccurrence(startForNext);
                                    sched.NextRunOn = TimeZoneInfo.ConvertTimeToUtc(nextOccurrenceLocal, timeZoneInfo);
                                }
                                catch (ArgumentException)
                                {
                                    // POWERFLOWS SAFETY BEHAVIOR — QUICKBASE PARITY UNVERIFIED
                                    // Defer nonexistent local hour (Spring Forward gap)
                                    var startForNext = sched.LastRunOn.HasValue
                                        ? TimeZoneInfo.ConvertTimeFromUtc(sched.LastRunOn.Value, timeZoneInfo).AddHours(1)
                                        : nowLocal.AddHours(1);
                                    var scheduleInstance = CrontabSchedule.Parse(sched.CronExpression);
                                    var nextOccurrenceLocal = scheduleInstance.GetNextOccurrence(startForNext);
                                    sched.NextRunOn = TimeZoneInfo.ConvertTimeToUtc(nextOccurrenceLocal, timeZoneInfo);
                                    _logger.LogWarning("DST Spring Forward: skipped invalid local time for schedule {ScheduleId}, advanced next occurrence.", sched.Id);
                                }
                                await pipelineRepo.UpdateScheduleAsync(sched, transaction: null, ct);
                            }

                            // If NextRunOn is in the future, skip
                            if (utcNow < sched.NextRunOn.Value)
                            {
                                continue;
                            }

                            // We are due! Trigger enqueuing
                            var latestOccurrenceUtc = sched.NextRunOn.Value;
                            var latestOccurrenceLocal = TimeZoneInfo.ConvertTimeFromUtc(latestOccurrenceUtc, timeZoneInfo);

                            // Compute NextRunOn strictly starting from nowLocal to prevent backlog/stampede
                            DateTime nextRunUtc;
                            try
                            {
                                var scheduleInstance2 = CrontabSchedule.Parse(sched.CronExpression);
                                var nextRunLocal = scheduleInstance2.GetNextOccurrence(nowLocal);
                                nextRunUtc = TimeZoneInfo.ConvertTimeToUtc(nextRunLocal, timeZoneInfo);
                            }
                            catch (ArgumentException)
                            {
                                // POWERFLOWS SAFETY BEHAVIOR — QUICKBASE PARITY UNVERIFIED
                                // Defer nonexistent local hour (Spring Forward gap)
                                var scheduleInstance2 = CrontabSchedule.Parse(sched.CronExpression);
                                var nextRunLocal = scheduleInstance2.GetNextOccurrence(nowLocal.AddHours(1));
                                nextRunUtc = TimeZoneInfo.ConvertTimeToUtc(nextRunLocal, timeZoneInfo);
                                _logger.LogWarning("DST Spring Forward: skipped invalid local time for schedule {ScheduleId}, advanced next run time.", sched.Id);
                            }

                            // Distributed CAS lock on schedule metadata
                            var pipeline = await pipelineRepo.GetByIdAsync(sched.PipelineId, ct);
                            if (pipeline == null || !pipeline.IsActive || pipeline.IsDeleted)
                            {
                                continue;
                            }

                            // Authoritative current-state check using centralized helper
                            var currentSteps = await pipelineRepo.GetStepsByPipelineIdAsync(sched.PipelineId, ct);
                            var activeSteps = currentSteps.Where(s => !s.IsDeleted).ToList();

                            if (!PowerBase.Application.Pipelines.PipelineScheduleEligibility.IsPipelineScheduleable(activeSteps))
                            {
                                _logger.LogWarning("Skipping scheduled enqueue for Schedule {ScheduleId} of Pipeline {PipelineId}: step configuration is not scheduleable.", sched.Id, sched.PipelineId);
                                continue;
                            }

                            Guid messageId;
                            if (pipeline != null)
                            {
                                var hashInput = pipeline.PublicId.ToString() + "_" + sched.PublicId.ToString() + "_" + latestOccurrenceUtc.ToString("o");
                                var hashBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(hashInput));
                                var guidBytes = new byte[16];
                                Array.Copy(hashBytes, guidBytes, 16);
                                messageId = new Guid(guidBytes);
                            }
                            else
                            {
                                messageId = Guid.NewGuid();
                            }

                            var task = new PipelineExecutionTask
                            {
                                TenantId = tenantId,
                                PipelineId = sched.PipelineId,
                                TriggerEvent = "pipeline_schedule",
                                TriggerPayloadJson = JsonSerializer.Serialize(new
                                {
                                    TriggerTime = latestOccurrenceUtc,
                                    ScheduledTime = latestOccurrenceLocal,
                                    CronExpression = sched.CronExpression
                                }),
                                TriggeredBy = 0,
                                VariablesJson = null,
                                CorrelationId = Guid.NewGuid().ToString(),
                                Depth = 1,
                                MessageId = messageId.ToString()
                            };

                            bool enqueueSuccess = false;
                            try
                            {
                                queue.QueueTask(task);
                                enqueueSuccess = true;
                            }
                            catch (PowerBase.Infrastructure.Pipelines.MessageDeduplicatedException)
                            {
                                _logger.LogInformation("Schedule occurrences deduplicated and treated as successful enqueue for Schedule {ScheduleId}.", sched.Id);
                                enqueueSuccess = true;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to enqueue scheduled execution for Schedule {ScheduleId}.", sched.Id);
                            }

                            if (enqueueSuccess)
                            {
                                await pipelineRepo.UpdateScheduleLastAndNextRunOnAsync(sched.Id, sched.LastRunOn, latestOccurrenceUtc, nextRunUtc, ct);
                                _logger.LogInformation("Enqueued Pipeline-Level scheduled execution for Schedule {ScheduleId} (Tenant {TenantId}) at scheduled local time {ScheduledTime}.", sched.Id, tenantId, latestOccurrenceLocal);
                            }
                        }
                        catch (Exception schedEx)
                        {
                            _logger.LogError(schedEx, "Error evaluating pipeline-level schedule {ScheduleId} for tenant {TenantId}.", sched.Id, tenantId);
                        }
                    }
                }
                finally
                {
                    // Release session app lock
                    try
                    {
                        const string releaseSql = "EXEC sp_releaseapplock @Resource = @resourceName, @LockOwner = 'Session';";
                        await lockConn.ExecuteAsync(releaseSql, new { resourceName });
                    }
                    catch (Exception releaseEx)
                    {
                        _logger.LogWarning(releaseEx, "Failed to release app lock for tenant {TenantId}.", tenantId);
                    }
                }
            }
            catch (Exception tenantEx)
            {
                _logger.LogError(tenantEx, "Error evaluating schedules for tenant {TenantId}.", tenantId);
            }
        }
    }


    private class SchedulerStepConfig
    {
        public string? CronExpression { get; set; }
        public string? TimeZone { get; set; }
    }
}
