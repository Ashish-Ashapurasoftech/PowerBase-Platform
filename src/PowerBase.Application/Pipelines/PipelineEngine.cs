using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Records;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Enums;
using PowerBase.Domain.Exceptions;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Microsoft.Extensions.Options;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Reports;
using Scriban;
using Scriban.Runtime;

namespace PowerBase.Application.Pipelines;
public class PipelineEngine : IPipelineEngine
{
    internal static Dictionary<string, object?> BuildBulkEventRecord(PipelineBulkEventRecord record)
    {
        var valuesJson = string.Equals(record.EventType, "Deleted", StringComparison.OrdinalIgnoreCase)
            ? record.BeforeValuesJson : record.AfterValuesJson;
        var result = string.IsNullOrWhiteSpace(valuesJson)
            ? new Dictionary<string, object?>()
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(valuesJson) ?? new();
        // Preserve Quickbase-style id and the identity used by saved action mappings.
        // Staging identity wins over any similarly named field in the snapshot.
        result["id"] = record.RecordPublicId.ToString();
        result["RecordPublicId"] = record.RecordPublicId.ToString();
        result["event_type"] = record.EventType;
        return result;
    }

    private readonly IPipelineRepository _pipelineRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IRecordWriteService _recordWriteService;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IEmailService _emailService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IFileStorageService _fileStorageService;
    private readonly PipelineExecutionOptions _options;
    private readonly ILogger<PipelineEngine> _logger;
    private readonly IPipelineTriggerInterceptor _triggerInterceptor;
    private readonly ITenantUnitOfWork _uow;
    private readonly IPipelineAuditFormatter _auditFormatter;
    private readonly IQueryContext _queryContext;
    private readonly Microsoft.Extensions.DependencyInjection.IServiceScopeFactory _serviceScopeFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly IAdminRepository _adminRepo;
    private readonly ITenantRepository _tenantRepo;
    private readonly IPipelineStepIdempotencyRepository _idempotencyRepo;
    private readonly IPipelineRecordSearchService _pipelineRecordSearchService;

    public PipelineEngine(
        IPipelineRepository pipelineRepo,
        IRecordRepository recordRepo,
        IRecordWriteService recordWriteService,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IEmailService emailService,
        IHttpClientFactory httpClientFactory,
        IFileStorageService fileStorageService,
        IOptions<PipelineExecutionOptions> options,
        ILogger<PipelineEngine> logger,
        IPipelineTriggerInterceptor triggerInterceptor,
        ITenantUnitOfWork uow,
        IPipelineAuditFormatter auditFormatter,
        IQueryContext queryContext,
        Microsoft.Extensions.DependencyInjection.IServiceScopeFactory serviceScopeFactory,
        IServiceProvider serviceProvider,
        IAdminRepository adminRepo,
        ITenantRepository tenantRepo,
        IPipelineStepIdempotencyRepository idempotencyRepo)
    {
        _pipelineRepo = pipelineRepo;
        _recordRepo = recordRepo;
        _recordWriteService = recordWriteService;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _emailService = emailService;
        _httpClientFactory = httpClientFactory;
        _fileStorageService = fileStorageService;
        _options = options.Value;
        _logger = logger;
        _triggerInterceptor = triggerInterceptor;
        _uow = uow;
        _auditFormatter = auditFormatter;
        _queryContext = queryContext;
        _serviceScopeFactory = serviceScopeFactory;
        _serviceProvider = serviceProvider;
        _adminRepo = adminRepo;
        _tenantRepo = tenantRepo;
        _idempotencyRepo = idempotencyRepo;
        _pipelineRecordSearchService = (IPipelineRecordSearchService)serviceProvider.GetService(typeof(IPipelineRecordSearchService))!;
    }
    public async Task ExecuteAsync(PipelineExecutionTask task, CancellationToken ct)
    {
        int attempt = 1;
        int maxAttempts = _options.SqlDeadlockMaxRetries;

        while (true)
        {
            try
            {
                await RunPipelineAttemptAsync(task, attempt, ct);
                break; // Success! Exit retry loop.
            }
            catch (Exception sqlEx) when (sqlEx.GetType().Name == "SqlException" && IsSqlDeadlock(sqlEx))
            {
                _logger.LogWarning(sqlEx, "SQL Server deadlock (1205) encountered during pipeline run. Attempt {Attempt} of {MaxAttempts}.", attempt, maxAttempts);

                if (attempt >= maxAttempts)
                {
                    _logger.LogError(sqlEx, "SQL Server deadlock retry limit reached ({MaxAttempts} attempts). Failing execution.", maxAttempts);
                    throw;
                }

                attempt++;
                // Randomized backoff delay (200ms - 500ms)
                var delay = Random.Shared.Next(200, 500);
                await Task.Delay(delay, ct);
            }
        }
    }

    private readonly string _workerId = $"worker_engine_{Guid.NewGuid()}";

    private class PipelineRunSuccessSkipException : Exception
    {
    }

    private async Task RunPipelineAttemptAsync(PipelineExecutionTask task, int attemptCount, CancellationToken ct)
    {
        var workerId = task.WorkerId ?? _workerId;
        _logger.LogInformation("Starting pipeline run attempt. PipelineId: {PipelineId}, TriggerEvent: {TriggerEvent}", task.PipelineId, task.TriggerEvent);

        long runId = 0;
        PipelineRun? run = null;
        Guid? messageGuid = null;
        bool resumingPause = false;

        if (!string.IsNullOrEmpty(task.MessageId) && Guid.TryParse(task.MessageId, out var parsedMsgId))
        {
            messageGuid = parsedMsgId;
        }

        if (messageGuid.HasValue)
        {
            // Idempotency/Claim logic for new-event trigger
            using (var suppressScope = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
            {
                run = await _pipelineRepo.GetRunByMessageIdAsync(messageGuid.Value, ct);
                if (run == null)
                {
                    try
                    {
                        run = new PipelineRun
                        {
                            PipelineId = task.PipelineId,
                            Status = "Running",
                            TriggerType = task.TriggerEvent,
                            StartedOn = DateTime.UtcNow,
                            TriggeredBy = task.TriggeredBy,
                            MessageId = messageGuid.Value,
                            LockedBy = workerId,
                            LockedUntil = DateTime.UtcNow.AddSeconds(45),
                            HeartbeatOn = DateTime.UtcNow,
                            AttemptCount = 1
                        };
                        var (_, dbRunId) = await _pipelineRepo.CreateRunAsync(run, ct);
                        runId = dbRunId;
                        run.Id = runId;
                    }
                    catch (Exception ex) when (ex.Message.Contains("UX_PipelineRun_MessageId") || ex.InnerException?.Message.Contains("UX_PipelineRun_MessageId") == true)
                    {
                        // Race lost on insert. Reload and proceed to existing run evaluation
                        run = await _pipelineRepo.GetRunByMessageIdAsync(messageGuid.Value, ct);
                    }
                }

                if (run != null)
                {
                    runId = run.Id;
                    if (run.Status == "Success" || run.Status == "Skipped" || run.Status == "Stopped")
                    {
                        _logger.LogInformation("Pipeline execution {MessageId} already completed with status {Status}. Skipping.", messageGuid.Value, run.Status);
                        suppressScope.Complete();
                        return; // ACK immediately
                    }
                    else if (run.Status == "Running")
                    {
                        if (run.LockedBy != workerId)
                        {
                            if (run.LockedUntil.HasValue && run.LockedUntil.Value > DateTime.UtcNow)
                            {
                                throw new PipelineRunRunningException($"Pipeline run {messageGuid.Value} is actively locked by worker {run.LockedBy} until {run.LockedUntil.Value:o}.");
                            }

                            // Lease expired: attempt stale reclaim
                            var reclaimed = await _pipelineRepo.ReclaimStaleRunAsync(messageGuid.Value, workerId, ct);
                            if (!reclaimed)
                            {
                                _logger.LogWarning("Failed to reclaim stale run lease for {MessageId}. Skipping.", messageGuid.Value);
                                suppressScope.Complete();
                                return;
                            }
                            run = await _pipelineRepo.GetRunByMessageIdAsync(messageGuid.Value, ct);
                        }
                    }
                    else if (run.Status == "Failed")
                    {
                        if (run.AttemptCount >= 5)
                        {
                            _logger.LogError("Retry limit exhausted (5 attempts) for run {MessageId}. Skipping.", messageGuid.Value);
                            suppressScope.Complete();
                            return;
                        }

                        // Claim failed run retry
                        var claimed = await _pipelineRepo.ClaimFailedRunRetryAsync(messageGuid.Value, workerId, ct);
                        if (!claimed)
                        {
                            _logger.LogWarning("Failed to claim failed run retry for {MessageId}. Skipping.", messageGuid.Value);
                            suppressScope.Complete();
                            return;
                        }
                        run = await _pipelineRepo.GetRunByMessageIdAsync(messageGuid.Value, ct);
                    }
                    else if (run.Status == "Waiting")
                    {
                        resumingPause = true;
                        // Claim waiting run back to running
                        var claimed = await _pipelineRepo.ClaimWaitingRunAsync(messageGuid.Value, workerId, ct);
                        if (!claimed)
                        {
                            _logger.LogWarning("Failed to claim waiting run for {MessageId}. Skipping.", messageGuid.Value);
                            suppressScope.Complete();
                            return;
                        }
                        run = await _pipelineRepo.GetRunByMessageIdAsync(messageGuid.Value, ct);
                    }
                }
                suppressScope.Complete();
            }
        }
        else
        {
            // Scheduled or legacy execution: create running run normally
            using (var suppressScope = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
            {
                run = new PipelineRun
                {
                    PipelineId = task.PipelineId,
                    Status = "Running",
                    TriggerType = task.TriggerEvent,
                    StartedOn = DateTime.UtcNow,
                    TriggeredBy = task.TriggeredBy
                };
                var (_, dbRunId) = await _pipelineRepo.CreateRunAsync(run, ct);
                runId = dbRunId;
                run.Id = runId;
                suppressScope.Complete();
            }
        }

        if (run == null) return;

        long attemptId = 0;
        using (var suppressScope = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
        {
            var attemptRow = new PipelineRunAttempt
            {
                PipelineRunId = run.Id,
                AttemptNumber = run.AttemptCount,
                Status = "Running"
            };
            attemptId = await _pipelineRepo.CreateRunAttemptAsync(attemptRow, ct);
            suppressScope.Complete();
        }

        // Start lease heartbeat loop if messageGuid is present
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? heartbeatTask = null;

        if (messageGuid.HasValue)
        {
            heartbeatTask = Task.Run(async () =>
            {
                while (!heartbeatCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(15000, heartbeatCts.Token);
                        using (var suppressScope = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
                        {
                            await _pipelineRepo.ExtendRunLeaseAsync(messageGuid.Value, workerId, heartbeatCts.Token);
                            suppressScope.Complete();
                        }
                    }
                    catch
                    {
                        // Ignore cancellation/extension failures
                    }
                }
            });
        }

        try
        {
            if (task.Depth > 10)
            {
                //throw new PipelineRecursionException($"Pipeline recursion limit exceeded. Depth is {task.Depth}. CorrelationId: {task.CorrelationId}");
                _logger.LogInformation("Pipeline recursion limit exceeded 10 (Depth is {Depth}). Continuing recursion indefinitely as configured.", task.Depth);
            }

            // Fetch steps
            var steps = await _pipelineRepo.GetStepsByPipelineIdAsync(task.PipelineId, ct);
            var activeSteps = steps.Where(s => !s.IsDeleted).ToList();

            await _auditFormatter.InitializeAsync(task.PipelineId, task.TriggeredBy, ct);

            var pipelineMeta = await _pipelineRepo.GetByIdAsync(task.PipelineId, ct);
            bool isSkipped = false;
            string? skipReason = null;

            if (pipelineMeta == null)
            {
                isSkipped = true;
                skipReason = "Missing Pipeline metadata";
            }
            else if (pipelineMeta.IsDeleted)
            {
                isSkipped = true;
                skipReason = "Pipeline is deleted";
            }

            // Mismatch validation before execution starts
            var rootStep = activeSteps.FirstOrDefault(s => s.ParentStepId == null);
            var firstQueryStep = rootStep;
            while (firstQueryStep != null && firstQueryStep.Subtype == "handle-errors")
            {
                firstQueryStep = activeSteps
                    .Where(s => s.ParentStepId == firstQueryStep.Id && string.Equals(s.ParentBranch, "children", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(s => s.DisplayOrder)
                    .ThenBy(s => s.Id)
                    .FirstOrDefault();
            }

            var eventName = task.TriggerEvent?.ToLowerInvariant() ?? "manual";
            if (!isSkipped && eventName == "pipeline-called" && task.TriggeredBy != pipelineMeta!.CreatedBy)
                throw new PipelineNonRetryableException("Callable execution must use the called pipeline owner's identity.");
            var normalizedEventName = eventName.Replace("-", "").Replace("_", "");
            if (normalizedEventName is "recordadded" or "recordupdated" or "recorddeleted" or "newevent" or "webhook")
            {
                normalizedEventName = "new-event";
            }
            else if (normalizedEventName == "newbulkevent")
            {
                normalizedEventName = "new-bulk-event";
            }
            else if (normalizedEventName == "pipelineschedule")
            {
                normalizedEventName = "pipeline_schedule";
            }

            if (!isSkipped && normalizedEventName != "manual")
            {
                if (normalizedEventName == "activation")
                {
                    var isQueryRoot = firstQueryStep?.Type == "query" && (firstQueryStep.Subtype == "search-records" || firstQueryStep.Subtype == "look-up-record");
                    var isPrepareBulkRoot = firstQueryStep?.Type == "action" && firstQueryStep.Subtype == "prepare-bulk-upsert";
                    var isCopyRecordsRoot = firstQueryStep?.Type == "action" && firstQueryStep.Subtype == "copy-records";
                    var isMakeRequestRoot = firstQueryStep?.Type == "action" && firstQueryStep.Subtype == "make-request";
                    var isPauseRoot = firstQueryStep?.Type == "action" && firstQueryStep.Subtype == "pause";
                    if (!isQueryRoot && !isPrepareBulkRoot && !isCopyRecordsRoot && !isMakeRequestRoot && !isPauseRoot)
                    {
                        throw new PowerBase.Domain.Exceptions.PipelineNonRetryableException("Activation trigger event requires a Search/Query, Make Request, Copy Records, Prepare Bulk Record Upsert, or Pause first step.");
                    }
                }
                else if (normalizedEventName == "pipeline_schedule")
                {
                    var isQueryRoot = firstQueryStep?.Type == "query" && (firstQueryStep.Subtype == "search-records" || firstQueryStep.Subtype == "look-up-record");
                    var isPrepareBulkRoot = firstQueryStep?.Type == "action" && firstQueryStep.Subtype == "prepare-bulk-upsert";
                    var isCopyRecordsRoot = firstQueryStep?.Type == "action" && firstQueryStep.Subtype == "copy-records";
                    var isMakeRequestRoot = firstQueryStep?.Type == "action" && firstQueryStep.Subtype == "make-request";
                    var isPauseRoot = firstQueryStep?.Type == "action" && firstQueryStep.Subtype == "pause";
                    if (!isQueryRoot && !isPrepareBulkRoot && !isCopyRecordsRoot && !isMakeRequestRoot && !isPauseRoot)
                    {
                        throw new PowerBase.Domain.Exceptions.PipelineNonRetryableException("Pipeline schedule trigger event requires a Search/Query, Make Request, Copy Records, Prepare Bulk Record Upsert, or Pause first step.");
                    }
                    if (activeSteps.Any(s => s.Type == "trigger"))
                    {
                        throw new PowerBase.Domain.Exceptions.PipelineNonRetryableException("Pipeline schedule trigger event is incompatible with trigger steps on the canvas.");
                    }
                }
                else if (normalizedEventName == "schedule")
                {
                    if (rootStep == null || rootStep.Type != "trigger" || rootStep.Subtype != "schedule")
                    {
                        throw new PowerBase.Domain.Exceptions.PipelineNonRetryableException("Schedule trigger event requires an active root-level schedule trigger step.");
                    }
                }
                else if (normalizedEventName == "new-bulk-event")
                {
                    if (rootStep == null || rootStep.Type != "trigger" || rootStep.Subtype != "new-bulk-event")
                    {
                        throw new PowerBase.Domain.Exceptions.PipelineNonRetryableException("New bulk event trigger event requires an active root-level new-bulk-event trigger step.");
                    }
                }
                else if (normalizedEventName == "new-event")
                {
                    if (rootStep == null || rootStep.Type != "trigger" || 
                        (rootStep.Subtype != "new-event" && rootStep.Subtype != "record-added" && rootStep.Subtype != "record-updated" && rootStep.Subtype != "record-deleted" && rootStep.Subtype != "webhook"))
                    {
                        throw new PowerBase.Domain.Exceptions.PipelineNonRetryableException("New event trigger event requires an active root-level event trigger step.");
                    }
                }
                else
                {
                    if (rootStep == null || rootStep.Type != "trigger" || !string.Equals(rootStep.Subtype, eventName, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new PowerBase.Domain.Exceptions.PipelineNonRetryableException($"Trigger event '{task.TriggerEvent}' mismatch with root step.");
                    }
                }
            }

            var triggerStep = activeSteps.FirstOrDefault(s => s.Type == "trigger" && 
                (s.Subtype == "new-event" || s.Subtype == "new-bulk-event" || s.Subtype == "record-added" || s.Subtype == "record-updated" || s.Subtype == "record-deleted" || s.Subtype == "webhook"));

            if (!isSkipped)
            {
                if (!pipelineMeta.IsActive)
                {
                    isSkipped = true;
                    skipReason = "Pipeline is inactive";
                }
                else
                {
                    bool isEventTrigger = normalizedEventName is "new-event" or "new-bulk-event";
                    if (isEventTrigger && triggerStep == null)
                    {
                        isSkipped = true;
                        skipReason = "Event execution missing required trigger step on canvas";
                    }
                }
            }

            if (isSkipped)
            {
                _logger.LogWarning("Pipeline run {PipelineId} attempt skipped. Reason: {SkipReason}", task.PipelineId, skipReason);
                using (var suppressScope = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
                {
                    run.Status = "Skipped";
                    run.ErrorMessage = $"Skipped: {skipReason}";
                    run.LockedBy = null;
                    run.LockedUntil = null;
                    run.CompletedOn = DateTime.UtcNow;
                    await _pipelineRepo.UpdateRunAsync(run, ct);

                    await _pipelineRepo.UpdateRunAttemptAsync(new PipelineRunAttempt
                    {
                        Id = attemptId,
                        PipelineRunId = run.Id,
                        AttemptNumber = run.AttemptCount,
                        Status = "Success",
                        LastError = $"Skipped: {skipReason}"
                    }, ct);
                    suppressScope.Complete();
                }
                return;
            }

            _logger.LogInformation("Pipeline {PipelineId} (Attempt {Attempt}) executing active steps. TriggerEvent: {TriggerEvent}", task.PipelineId, run.AttemptCount, task.TriggerEvent);

            var snapshots = new List<RawStepAuditSnapshot>();
            bool txSuccess = false;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var contextDict = new Dictionary<string, object>();
                contextDict["_CorrelationId"] = task.CorrelationId ?? "";
                contextDict["_Depth"] = task.Depth;
                contextDict["_CreatedBy"] = pipelineMeta.CreatedBy;
                contextDict["_MessageId"] = messageGuid ?? Guid.Empty;
                contextDict["_ResumeAfterPause"] = resumingPause;
                var stepsDict = new Dictionary<string, object>();
                contextDict["steps"] = stepsDict;

                if (!string.IsNullOrEmpty(task.TriggerPayloadJson))
                {
                    try
                    {
                        var triggerData = JsonSerializer.Deserialize<Dictionary<string, object>>(task.TriggerPayloadJson);
                        if (triggerData != null)
                        {
                            contextDict["trigger"] = triggerData;
                            foreach (var kvp in triggerData)
                            {
                                contextDict[kvp.Key] = kvp.Value;
                            }

                            if (triggerData.TryGetValue("TriggerStepRefId", out var refIdObj) && refIdObj != null)
                            {
                                var refId = refIdObj.ToString();
                                if (!string.IsNullOrEmpty(refId))
                                {
                                    if (triggerData.TryGetValue("SelectedFieldValues", out var selectedValuesObj) && selectedValuesObj is JsonElement selectedElement)
                                    {
                                        var selectedDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(selectedElement.GetRawText());
                                        if (selectedDict != null)
                                        {
                                            if (triggerData.TryGetValue("RecordPublicId", out var recIdObj) && recIdObj != null)
                                            {
                                                selectedDict["RecordPublicId"] = recIdObj.ToString();
                                                selectedDict["id"] = recIdObj.ToString();
                                                selectedDict["publicId"] = recIdObj.ToString();
                                            }
                                            stepsDict[refId] = selectedDict;
                                        }
                                    }
                                }
                            }

                            if (task.TriggerEvent == "new-bulk-event" && messageGuid.HasValue)
                            {
                                var bulkEventPreview = await _pipelineRepo.GetBulkEventRecordsPreviewAsync(messageGuid.Value, 100, ct);
                                var count = 0;
                                if (triggerData.TryGetValue("Count", out var countObj) && countObj != null && int.TryParse(countObj.ToString(), out var parsedCount))
                                {
                                    count = parsedCount;
                                }

                                var recordsList = new List<Dictionary<string, object?>>();
                                foreach (var r in bulkEventPreview)
                                {
                                    var recordObj = BuildBulkEventRecord(r);

                                    recordsList.Add(recordObj);
                                }

                                var bulkTriggerObj = new Dictionary<string, object>();
                                bulkTriggerObj["records"] = recordsList;
                                bulkTriggerObj["count"] = count;
                                bulkTriggerObj["MessageId"] = messageGuid.Value.ToString();
                                bulkTriggerObj["Status"] = "Fired";
                                bulkTriggerObj["TriggerType"] = "On New Bulk Event";
                                if (triggerData.TryGetValue("TriggerStepRefId", out var refIdObj2) && refIdObj2 != null)
                                {
                                    bulkTriggerObj["TriggerStepRefId"] = refIdObj2.ToString()!;
                                }

                                contextDict["trigger"] = bulkTriggerObj;
                                stepsDict["trigger"] = bulkTriggerObj;
                                if (triggerData.TryGetValue("TriggerStepRefId", out var refIdObj3) && refIdObj3 != null)
                                {
                                    var refId = refIdObj3.ToString();
                                    if (!string.IsNullOrEmpty(refId))
                                    {
                                        stepsDict[refId] = bulkTriggerObj;
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to parse trigger payload JSON for context propagation.");
                    }
                }

                if (!string.IsNullOrEmpty(task.VariablesJson))
                {
                    try
                    {
                        var variablesData = JsonSerializer.Deserialize<Dictionary<string, object>>(task.VariablesJson);
                        if (variablesData != null)
                        {
                            contextDict["variables"] = variablesData;
                            foreach (var kvp in variablesData)
                            {
                                contextDict[kvp.Key] = kvp.Value;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to parse variables JSON for context propagation.");
                    }
                }

                // Execute the root steps hierarchically
                await ExecuteSiblingStepsAsync(runId, activeSteps, null, null, contextDict, stepsDict, snapshots, "root", ct);
                txSuccess = true;
            }
            catch (PowerBase.Domain.Exceptions.PipelineWaitException)
            {
                // Pausing preserves all steps completed so far; they were not rolled back.
                txSuccess = true;
                throw;
            }
            finally
            {
                var executionTimeMs = sw.ElapsedMilliseconds;
                _logger.LogInformation("Pipeline transaction finished. Business execution completed in {ExecutionTimeMs} ms. Success={Success}", executionTimeMs, txSuccess);

                sw.Restart();
                foreach (var snap in snapshots)
                {
                    try
                    {
                        var corrId = task.CorrelationId ?? "";
                        
                        var (formattedInput, formattedOutput, logMsg) = _auditFormatter.FormatStepRun(
                            snap.Step,
                            snap.RawInputJson,
                            snap.RawOutputJson,
                            snap.Status,
                            corrId,
                            snap.StartedOn,
                            snap.CompletedOn
                        );

                        if (!txSuccess)
                        {
                            snap.RolledBack = true;
                            logMsg = $"[Rolled Back] {logMsg}";

                            try
                            {
                                var outDict = JsonSerializer.Deserialize<Dictionary<string, object>>(formattedOutput);
                                if (outDict != null)
                                {
                                    if (outDict.TryGetValue("TechnicalDetails", out var tdObj) && tdObj is JsonElement tdEl)
                                    {
                                        var tdDict = JsonSerializer.Deserialize<Dictionary<string, object>>(tdEl.GetRawText()) ?? new Dictionary<string, object>();
                                        tdDict["TransactionOutcome"] = "Rolled Back";
                                        outDict["TechnicalDetails"] = tdDict;
                                        formattedOutput = JsonSerializer.Serialize(outDict);
                                    }
                                }
                            }
                            catch {}
                        }

                        snap.StepRun.InputContext = formattedInput;
                        snap.StepRun.OutputContext = formattedOutput;
                        snap.StepRun.LogMessage = !string.IsNullOrEmpty(logMsg) ? logMsg : $"Step execution finished with status {snap.Status}.";
                        snap.StepRun.CompletedOn = snap.CompletedOn;

                        using (var suppressScope = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
                        {
                            await _pipelineRepo.UpdateStepRunAsync(snap.StepRun, ct);
                            suppressScope.Complete();
                        }
                    }
                    catch (Exception fmtEx)
                    {
                        _logger.LogError(fmtEx, "Formatter failed for step {StepId} of run {RunId}. Proceeding with raw values.", snap.Step.Id, runId);
                    }
                }

                var formattingTimeMs = sw.ElapsedMilliseconds;
                _logger.LogInformation("Friendly audit formatting completed in {FormattingTimeMs} ms.", formattingTimeMs);
            }

            // Mark pipeline run success (Suppress to write immediately)
            using (var suppressScope = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
            {
                bool holdLease = true;
                if (messageGuid.HasValue)
                {
                    var freshRun = await _pipelineRepo.GetRunByMessageIdAsync(messageGuid.Value, ct);
                    if (freshRun == null || freshRun.LockedBy != workerId)
                    {
                        holdLease = false;
                        _logger.LogWarning("Worker lost lease for {MessageId}. Skipping status completion write.", messageGuid.Value);
                    }
                }

                if (holdLease)
                {
                    run.Status = "Success";
                    run.LockedBy = null;
                    run.LockedUntil = null;
                    run.CompletedOn = DateTime.UtcNow;
                    await _pipelineRepo.UpdateRunAsync(run, ct);

                    await _pipelineRepo.UpdateRunAttemptAsync(new PipelineRunAttempt
                    {
                        Id = attemptId,
                        PipelineRunId = run.Id,
                        AttemptNumber = run.AttemptCount,
                        Status = "Success"
                    }, ct);
                }
                suppressScope.Complete();
            }
            _logger.LogInformation("Pipeline {PipelineId} execution completed successfully.", task.PipelineId);
        }
        catch (PowerBase.Domain.Exceptions.PipelineWaitException waitEx)
        {
            using (var suppressScope = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
            {
                bool holdLease = true;
                if (messageGuid.HasValue)
                {
                    var freshRun = await _pipelineRepo.GetRunByMessageIdAsync(messageGuid.Value, ct);
                    if (freshRun == null || freshRun.LockedBy != workerId) holdLease = false;
                }

                if (holdLease)
                {
                    run.Status = "Waiting";
                    run.ErrorMessage = $"Paused until {waitEx.ResumeDate:O}";
                    run.LockedBy = null;
                    run.LockedUntil = null;
                    // Intentionally leave CompletedOn unchanged/null because it's not complete.
                    await _pipelineRepo.UpdateRunAsync(run, ct);

                    await _pipelineRepo.UpdateRunAttemptAsync(new PipelineRunAttempt
                    {
                        Id = attemptId,
                        PipelineRunId = run.Id,
                        AttemptNumber = run.AttemptCount,
                        Status = "Waiting",
                        LastError = $"Paused until {waitEx.ResumeDate:O}"
                    }, ct);
                }
                suppressScope.Complete();
            }
            throw; // Bubble up to queue worker
        }
        catch (PipelineStopExecutionException stopEx)
        {
            // Gracefully handled (Stop commits and returns)
            using (var suppressScope = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
            {
                bool holdLease = true;
                if (messageGuid.HasValue)
                {
                    var freshRun = await _pipelineRepo.GetRunByMessageIdAsync(messageGuid.Value, ct);
                    if (freshRun == null || freshRun.LockedBy != workerId) holdLease = false;
                }

                if (holdLease)
                {
                    run.Status = "Stopped";
                    run.ErrorMessage = stopEx.Message;
                    run.LockedBy = null;
                    run.LockedUntil = null;
                    run.CompletedOn = DateTime.UtcNow;
                    await _pipelineRepo.UpdateRunAsync(run, ct);

                    await _pipelineRepo.UpdateRunAttemptAsync(new PipelineRunAttempt
                    {
                        Id = attemptId,
                        PipelineRunId = run.Id,
                        AttemptNumber = run.AttemptCount,
                        Status = "Success",
                        LastError = stopEx.Message
                    }, ct);
                }
                suppressScope.Complete();
            }
        }
        catch (PipelineRecursionException recEx)
        {
            _logger.LogError(recEx, "Pipeline {PipelineId} recursion limit exceeded.", task.PipelineId);
            using (var suppressScope = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
            {
                bool holdLease = true;
                if (messageGuid.HasValue)
                {
                    var freshRun = await _pipelineRepo.GetRunByMessageIdAsync(messageGuid.Value, ct);
                    if (freshRun == null || freshRun.LockedBy != workerId) holdLease = false;
                }

                if (holdLease)
                {
                    run.Status = "Failed";
                    run.ErrorMessage = recEx.Message;
                    run.LockedBy = null;
                    run.LockedUntil = null;
                    run.CompletedOn = DateTime.UtcNow;
                    await _pipelineRepo.UpdateRunAsync(run, ct);

                    await _pipelineRepo.UpdateRunAttemptAsync(new PipelineRunAttempt
                    {
                        Id = attemptId,
                        PipelineRunId = run.Id,
                        AttemptNumber = run.AttemptCount,
                        Status = "Failed",
                        LastError = recEx.Message
                    }, ct);
                }
                suppressScope.Complete();
            }

            try
            {
                await _emailService.SendRecursionAlertEmailAsync(task.PipelineId, task.CorrelationId ?? "", task.Depth, recEx.Message, ct);
            }
            catch (Exception emailEx)
            {
                _logger.LogError(emailEx, "Failed to send recursion email alert.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipeline {PipelineId} run aborted/failed.", task.PipelineId);
            using (var suppressScope = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
            {
                bool holdLease = true;
                if (messageGuid.HasValue)
                {
                    var freshRun = await _pipelineRepo.GetRunByMessageIdAsync(messageGuid.Value, ct);
                    if (freshRun == null || freshRun.LockedBy != workerId) holdLease = false;
                }

                if (holdLease)
                {
                    run.Status = "Failed";
                    run.ErrorMessage = ex.Message;
                    run.LockedBy = null;
                    run.LockedUntil = null;
                    run.CompletedOn = DateTime.UtcNow;
                    await _pipelineRepo.UpdateRunAsync(run, ct);

                    await _pipelineRepo.UpdateRunAttemptAsync(new PipelineRunAttempt
                    {
                        Id = attemptId,
                        PipelineRunId = run.Id,
                        AttemptNumber = run.AttemptCount,
                        Status = "Failed",
                        LastError = ex.Message
                    }, ct);
                }
                suppressScope.Complete();
            }
            throw;
        }
        finally
        {
            if (heartbeatTask != null)
            {
                heartbeatCts.Cancel();
                try
                {
                    await heartbeatTask;
                }
                catch
                {
                    // Ignore background task exceptions
                }
            }
        }
    }

    private async Task ExecuteSiblingStepsAsync(
        long runId,
        List<PipelineStep> allSteps,
        long? parentStepId,
        string? parentBranch,
        Dictionary<string, object> contextDict,
        Dictionary<string, object> stepsDict,
        List<RawStepAuditSnapshot> snapshots,
        string executionPath,
        CancellationToken ct)
    {
        var siblings = allSteps
            .Where(s => s.ParentStepId == parentStepId && (parentStepId == null || string.Equals(s.ParentBranch, parentBranch, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(s => s.DisplayOrder)
            .ToList();

        foreach (var step in siblings)
        {
            var pipeline = await _pipelineRepo.GetByIdAsync(step.PipelineId, ct);
            if (pipeline != null && pipeline.IsDeleted)
            {
                throw new PipelineStopExecutionException("Execution halted: Pipeline was deleted.");
            }
            else if (pipeline != null && !pipeline.IsActive)
            {
                throw new PipelineStopExecutionException("Execution halted: Pipeline was deactivated.");
            }

            var currentPath = $"{executionPath}/{step.RefId}";
            var replayable = step.Type is "action" or "query" or "trigger"
                && step.Subtype is not ("pause" or "prepare-bulk-upsert" or "commit-upsert");
            var messageId = contextDict.TryGetValue("_MessageId", out var messageObj) && messageObj is Guid guid
                ? guid : Guid.Empty;
            var pathHash = ComputeSha256Hash(currentPath);
            if (replayable && messageId != Guid.Empty &&
                contextDict.TryGetValue("_ResumeAfterPause", out var resumeObj) && resumeObj is true)
            {
                var previousOutput = await _idempotencyRepo.GetByExecutionKeyAsync(messageId, step.PublicId, pathHash, null, ct);
                if (previousOutput != null)
                {
                    if (step.Type != "trigger" && !string.IsNullOrEmpty(step.RefId))
                        stepsDict[step.RefId] = JsonSerializer.Deserialize<object>(previousOutput)!;
                    continue;
                }
            }

            var stepRun = new PipelineStepRun
            {
                PipelineRunId = runId,
                StepId = step.Id,
                Status = "Running",
                StartedOn = DateTime.UtcNow,
                InputContext = SerializeAndSanitizeAudit(contextDict)
            };

            long stepRunId = 0;
            using (var suppressScope = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
            {
                stepRunId = await _pipelineRepo.CreateStepRunAsync(stepRun, ct);
                stepRun.Id = stepRunId;
                suppressScope.Complete();
            }

            var corrId = contextDict.TryGetValue("_CorrelationId", out var cIdObj) ? cIdObj?.ToString() ?? string.Empty : string.Empty;

            try
            {
                var contextJson = JsonSerializer.Serialize(contextDict);
                var output = await ExecuteStepAsync(step, contextJson, contextDict, allSteps, stepsDict, runId, stepRun, snapshots, currentPath, ct);

                if (replayable && messageId != Guid.Empty && output != null &&
                    await _idempotencyRepo.GetByExecutionKeyAsync(messageId, step.PublicId, pathHash, null, ct) == null)
                {
                    await _idempotencyRepo.InsertAsync(new PipelineStepIdempotencyLog
                    {
                        MessageId = messageId,
                        StepPublicId = step.PublicId,
                        ExecutionPathHash = pathHash,
                        ExecutionPath = currentPath,
                        OutputJson = output
                    }, null, ct);
                }

                if (step.Type == "trigger")
                {
                    stepRun.Status = "Success";
                }
                else if (output != null && (output.Contains("\"Status\":\"Skipped\"") || output.Contains("\"status\":\"Skipped\"")))
                {
                    stepRun.Status = "Skipped";
                }
                else
                {
                    stepRun.Status = "Success";
                }

                stepRun.CompletedOn = DateTime.UtcNow;

                snapshots.Add(new RawStepAuditSnapshot
                {
                    Step = step,
                    StepRun = stepRun,
                    RawInputJson = stepRun.InputContext,
                    RawOutputJson = output,
                    Status = stepRun.Status,
                    StartedOn = stepRun.StartedOn,
                    CompletedOn = stepRun.CompletedOn.Value
                });

                using (var suppressScope = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
                {
                    await _pipelineRepo.UpdateStepRunAsync(new PipelineStepRun
                    {
                        Id = stepRun.Id,
                        PipelineRunId = stepRun.PipelineRunId,
                        StepId = stepRun.StepId,
                        Status = stepRun.Status,
                        StartedOn = stepRun.StartedOn,
                        CompletedOn = stepRun.CompletedOn,
                        LogMessage = $"Step executed. Status: {stepRun.Status}."
                    }, ct);
                    suppressScope.Complete();
                }

                if (!string.IsNullOrEmpty(step.RefId) && !string.IsNullOrEmpty(output) && step.Type != "trigger")
                {
                    try
                    {
                        var outputObj = JsonSerializer.Deserialize<object>(output);
                        if (outputObj != null)
                        {
                            stepsDict[step.RefId] = outputObj;
                        }
                    }
                    catch
                    {
                        stepsDict[step.RefId] = output;
                    }
                }
            }
            catch (PowerBase.Domain.Exceptions.PipelineWaitException waitEx)
            {
                stepRun.Status = "Waiting";
                stepRun.CompletedOn = DateTime.UtcNow;

                var output = JsonSerializer.Serialize(new { Status = "Waiting", ResumeDate = waitEx.ResumeDate });
                snapshots.Add(new RawStepAuditSnapshot
                {
                    Step = step,
                    StepRun = stepRun,
                    RawInputJson = stepRun.InputContext,
                    RawOutputJson = output,
                    Status = stepRun.Status,
                    StartedOn = stepRun.StartedOn,
                    CompletedOn = stepRun.CompletedOn.Value
                });

                using (var suppressScope = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
                {
                    await _pipelineRepo.UpdateStepRunAsync(new PipelineStepRun
                    {
                        Id = stepRun.Id,
                        PipelineRunId = stepRun.PipelineRunId,
                        StepId = stepRun.StepId,
                        Status = stepRun.Status,
                        StartedOn = stepRun.StartedOn,
                        CompletedOn = stepRun.CompletedOn,
                        LogMessage = $"Step paused until {waitEx.ResumeDate:O}"
                    }, ct);
                    suppressScope.Complete();
                }
                throw;
            }
            catch (PipelineStopExecutionException stopEx)
            {
                stepRun.Status = "Stopped";
                stepRun.CompletedOn = DateTime.UtcNow;

                var stopOutput = JsonSerializer.Serialize(new { Status = "Stopped", Reason = stopEx.Message });

                snapshots.Add(new RawStepAuditSnapshot
                {
                    Step = step,
                    StepRun = stepRun,
                    RawInputJson = stepRun.InputContext,
                    RawOutputJson = stopOutput,
                    Status = "Stopped",
                    StartedOn = stepRun.StartedOn,
                    CompletedOn = stepRun.CompletedOn.Value
                });

                using (var suppressScope = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
                {
                    await _pipelineRepo.UpdateStepRunAsync(new PipelineStepRun
                    {
                        Id = stepRun.Id,
                        PipelineRunId = stepRun.PipelineRunId,
                        StepId = stepRun.StepId,
                        Status = "Stopped",
                        StartedOn = stepRun.StartedOn,
                        CompletedOn = stepRun.CompletedOn,
                        LogMessage = $"Step stopped: {stopEx.Message}"
                    }, ct);
                    suppressScope.Complete();
                }
                throw;
            }
            catch (Exception stepEx)
            {
                _logger.LogError("Pipeline step {StepId} ({Subtype}) failed: {Reason}",
                    step.Id, step.Subtype, SanitizeErrorMessage(stepEx.Message));
                stepRun.Status = "Failed";
                stepRun.CompletedOn = DateTime.UtcNow;

                var errorInfo = new Dictionary<string, object?>();
                errorInfo["ErrorMessage"] = stepEx.Message;
                errorInfo["ExceptionType"] = stepEx.GetType().Name;
                errorInfo["StepId"] = step.Id;
                errorInfo["RefId"] = step.RefId;
                if (stepEx.InnerException != null)
                {
                    errorInfo["InnerError"] = stepEx.InnerException.Message;
                }
                var errorOutput = JsonSerializer.Serialize(errorInfo);

                snapshots.Add(new RawStepAuditSnapshot
                {
                    Step = step,
                    StepRun = stepRun,
                    RawInputJson = stepRun.InputContext,
                    RawOutputJson = errorOutput,
                    Status = "Failed",
                    StartedOn = stepRun.StartedOn,
                    CompletedOn = stepRun.CompletedOn.Value
                });

                using (var suppressScope = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
                {
                    await _pipelineRepo.UpdateStepRunAsync(new PipelineStepRun
                    {
                        Id = stepRun.Id,
                        PipelineRunId = stepRun.PipelineRunId,
                        StepId = stepRun.StepId,
                        Status = "Failed",
                        StartedOn = stepRun.StartedOn,
                        CompletedOn = stepRun.CompletedOn,
                        LogMessage = $"Step failed: {stepEx.Message}"
                    }, ct);
                    suppressScope.Complete();
                }
                throw;
            }
        }
    }

    private async Task<string> ExecuteStepAsync(
        PipelineStep step,
        string payloadJson,
        Dictionary<string, object> contextDict,
        List<PipelineStep> allSteps,
        Dictionary<string, object> stepsDict,
        long runId,
        PipelineStepRun stepRun,
        List<RawStepAuditSnapshot> snapshots,
        string executionPath,
        CancellationToken ct)
    {
        string? connectionPublicId = null;
        if (!string.IsNullOrEmpty(step.ConfigJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(step.ConfigJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("connectionPublicId", out var prop1) && prop1.ValueKind == JsonValueKind.String)
                    connectionPublicId = prop1.GetString();
                else if (root.TryGetProperty("ConnectionPublicId", out var prop2) && prop2.ValueKind == JsonValueKind.String)
                    connectionPublicId = prop2.GetString();
            }
            catch
            {
                // ignore config parse errors
            }
        }

        var isPowerBaseRequest = step.Subtype == "make-request" && MakeRequestDefinition.Read(step.ConfigJson ?? "{}").IsPowerBase;
        if (isPowerBaseRequest)
        {
            var requestConfig = MakeRequestDefinition.Read(step.ConfigJson ?? "{}");
            connectionPublicId ??= requestConfig.Connection;
            if (!Guid.TryParse(connectionPublicId, out var accountId) || PipelineStepValidator.SystemConnectionIds.Contains(accountId))
                throw new PipelineNonRetryableException("A valid PowerBase account is required.");
        }
        long createdBy = contextDict.TryGetValue("_CreatedBy", out var cbObj) && cbObj is long cbVal ? cbVal : 0L;
        Guid messageGuid = contextDict.TryGetValue("_MessageId", out var msgObj) && msgObj is Guid msgGuid ? msgGuid : Guid.Empty;

        var ownerTenantId = _queryContext.TenantId;
        var targetTenantId = ownerTenantId;
        bool isCrossTenant = false;
        Connections.Common.ConnectionScope? accountScope = null;

        if (Guid.TryParse(connectionPublicId, out var connectionGuid) && !PipelineStepValidator.SystemConnectionIds.Contains(connectionGuid))
        {
            var resolvedTenantId = await _adminRepo.GetTenantIdByPublicIdAsync(connectionGuid, ct);
            if (resolvedTenantId.HasValue)
            {
                targetTenantId = resolvedTenantId.Value;
                isCrossTenant = targetTenantId != ownerTenantId;
            }
            else
            {
                // Not one of the platform's tenants — it may be a saved PowerFlows account
                // ("Connect new account"). Such a step must run in the account's own realm as the
                // account's token owner; running it here would silently write the owner's realm.
                // Resolution happens in THIS scope on purpose: meta.PipelineAccount lives in the
                // owner tenant's database, and _queryContext still points at it.
                var connectionScopeResolver = _serviceProvider.GetService<Connections.Common.ConnectionScopeResolver>();
                if (connectionScopeResolver != null)
                {
                    // The execution authority (_CreatedBy) owns the account row, not the caller.
                    // A stale credential throws UnauthorizedActionException — the step fails, it
                    // never degrades to the owner tenant.
                    accountScope = await connectionScopeResolver.TryResolveForUserAsync(connectionGuid, createdBy, ct);
                    if (accountScope != null)
                    {
                        targetTenantId = accountScope.TargetTenantId;
                    }
                }
            }
        }

        if (isPowerBaseRequest && accountScope == null)
        {
            if (!Guid.TryParse(connectionPublicId, out var requestAccount) ||
                await _tenantRepo.GetTenantForUserAsync(requestAccount, createdBy, ct) == null)
                throw new UnauthorizedAccessException("The selected PowerBase account is unavailable to the pipeline owner.");
        }
        if (accountScope != null)
        {
            // TargetTenantScopeHelper pins the realm, adopts the token owner's identity and
            // permissions, and carries the token's app restrictions into the scope.
            await using var accountScopeHandle = await Connections.Common.TargetTenantScopeHelper.OpenAsync(_serviceScopeFactory, accountScope, ct);

            var scopedQueryContext = accountScopeHandle.GetRequiredService<IQueryContext>();
            scopedQueryContext.IsPipelineExecution = _queryContext.IsPipelineExecution;
            scopedQueryContext.PipelineDepth = _queryContext.PipelineDepth;
            scopedQueryContext.PipelineChainJson = _queryContext.PipelineChainJson;

            if (step.Subtype == "copy-records")
                return await ExecuteCopyRecordsAsync(step, payloadJson, allSteps, contextDict, executionPath, stepRun, accountScopeHandle.Services, ct);

            if (step.Subtype == "make-request")
                return await ExecuteMakeRequestAsync(step, payloadJson, allSteps, contextDict, executionPath, stepRun, accountScopeHandle.Services, ct);
            var accountRecordRepo = accountScopeHandle.GetRequiredService<IRecordRepository>();
            var accountTableRepo = accountScopeHandle.GetRequiredService<IAppTableRepository>();
            var accountFieldRepo = accountScopeHandle.GetRequiredService<IAppFieldRepository>();
            var accountWriteService = accountScopeHandle.GetRequiredService<IRecordWriteService>();
            var accountTriggerInterceptor = accountScopeHandle.GetRequiredService<IPipelineTriggerInterceptor>();
            var accountUow = accountScopeHandle.GetRequiredService<ITenantUnitOfWork>();
            var accountIdempotencyRepo = accountScopeHandle.GetRequiredService<IPipelineStepIdempotencyRepository>();
            var accountFileStorage = accountScopeHandle.GetRequiredService<IFileStorageService>();
            var accountRecordSearchService = accountScopeHandle.Services.GetService<IPipelineRecordSearchService>() ?? _pipelineRecordSearchService;

            return await ExecuteStepWithServicesAsync(step, payloadJson, contextDict, allSteps, stepsDict, runId, stepRun, snapshots, executionPath,
                accountRecordRepo, accountTableRepo, accountFieldRepo, accountWriteService, accountTriggerInterceptor, accountUow, accountIdempotencyRepo, accountFileStorage, accountRecordSearchService, ct);
        }

        if (isCrossTenant)
        {
            using (var scope = _serviceScopeFactory.CreateScope())
            {
                var scopedQueryContext = scope.ServiceProvider.GetRequiredService<IQueryContext>();
                scopedQueryContext.SetTenantId(targetTenantId);
                scopedQueryContext.IsPipelineExecution = _queryContext.IsPipelineExecution;
                scopedQueryContext.PipelineDepth = _queryContext.PipelineDepth;
                scopedQueryContext.PipelineChainJson = _queryContext.PipelineChainJson;
                scopedQueryContext.SetUserIdentity(
                    _queryContext.UserId,
                    _queryContext.IsSuperAdmin,
                    _queryContext.UserName,
                    _queryContext.UserEmail,
                    _queryContext.Permissions,
                    _queryContext.TenantRole);

                var scopedTenantRepo = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
                var isMember = await scopedTenantRepo.IsActiveMemberAsync(createdBy, ct);
                if (!isMember)
                {
                    throw new UnauthorizedAccessException($"Execution authority user {createdBy} is not an active member of target tenant {targetTenantId}.");
                }

            if (step.Subtype == "copy-records")
                return await ExecuteCopyRecordsAsync(step, payloadJson, allSteps, contextDict, executionPath, stepRun, scope.ServiceProvider, ct);

                if (step.Subtype == "make-request")
                    return await ExecuteMakeRequestAsync(step, payloadJson, allSteps, contextDict, executionPath, stepRun, scope.ServiceProvider, ct);
                var scopedRecordRepo = scope.ServiceProvider.GetRequiredService<IRecordRepository>();
                var scopedTableRepo = scope.ServiceProvider.GetRequiredService<IAppTableRepository>();
                var scopedFieldRepo = scope.ServiceProvider.GetRequiredService<IAppFieldRepository>();
                var scopedWriteService = scope.ServiceProvider.GetRequiredService<IRecordWriteService>();
                var scopedTriggerInterceptor = scope.ServiceProvider.GetRequiredService<IPipelineTriggerInterceptor>();
                var scopedUow = scope.ServiceProvider.GetRequiredService<ITenantUnitOfWork>();
                var scopedIdempotencyRepo = scope.ServiceProvider.GetRequiredService<IPipelineStepIdempotencyRepository>();
                var scopedFileStorage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
                var scopedRecordSearchService = scope.ServiceProvider.GetService<IPipelineRecordSearchService>() ?? _pipelineRecordSearchService;

                return await ExecuteStepWithServicesAsync(step, payloadJson, contextDict, allSteps, stepsDict, runId, stepRun, snapshots, executionPath,
                    scopedRecordRepo, scopedTableRepo, scopedFieldRepo, scopedWriteService, scopedTriggerInterceptor, scopedUow, scopedIdempotencyRepo, scopedFileStorage, scopedRecordSearchService, ct);
            }
        }
        else
        {
            if (step.Subtype == "copy-records")
                return await ExecuteCopyRecordsAsync(step, payloadJson, allSteps, contextDict, executionPath, stepRun, _serviceProvider, ct);
            return await ExecuteStepWithServicesAsync(step, payloadJson, contextDict, allSteps, stepsDict, runId, stepRun, snapshots, executionPath,
                _recordRepo, _tableRepo, _fieldRepo, _recordWriteService, _triggerInterceptor, _uow, _idempotencyRepo, _fileStorageService, _pipelineRecordSearchService, ct);
        }
    }

    private async Task<string> ExecuteCopyRecordsAsync(PipelineStep step, string payloadJson,
        List<PipelineStep> allSteps, Dictionary<string, object> contextDict, string executionPath,
        PipelineStepRun stepRun, IServiceProvider services, CancellationToken ct)
    {
        Guid messageId = contextDict.TryGetValue("_MessageId", out var value) && value is Guid id ? id : Guid.Empty;
            var config = CopyRecordsDefinition.Read(step.ConfigJson ?? "{}");
            var query = Regex.Replace(config.AdvancedQuery ?? "", @"\{\{.*?\}\}", match =>
                EvaluateTokens(match.Value, payloadJson, executionPath, allSteps)
                    .Replace("\\", "\\\\").Replace("'", "\\'"));
            stepRun.InputContext = SerializeAndSanitizeAudit(new { config.SourceTable, config.DestinationTable,
                config.SourceFields, config.DestinationFields, config.MergeField, config.TerminateOnError });
            return await new CopyRecordsExecutor(services)
                .ExecuteAsync(config, query, step.PublicId, messageId, executionPath, ct);
    }

    private async Task<string> ExecuteMakeRequestAsync(PipelineStep step, string payloadJson, List<PipelineStep> allSteps,
        Dictionary<string, object> context, string executionPath, PipelineStepRun stepRun, IServiceProvider services, CancellationToken ct)
    {
        var config = MakeRequestDefinition.Read(step.ConfigJson ?? "{}");
        string Resolve(string? value) => EvaluateTokens(value, payloadJson, executionPath, allSteps);
        if (!config.IsPowerBase && config.HttpConnectionId.HasValue)
        {
            var owner = context.GetValueOrDefault("_CreatedBy") is long id ? id : _queryContext.UserId;
            var saved = await services.GetRequiredService<IRequestConnectionService>().ResolveAsync(config.HttpConnectionId.Value, step.PipelineId, owner, ct);
            config.BaseUrl = saved.BaseUrl; config.ConnectionHeaders = saved.ConnectionHeaders; config.AuthType = saved.AuthType;
            config.Username = saved.Username; config.Password = saved.Password; config.BearerToken = saved.BearerToken;
            config.ApiKeyName = saved.ApiKeyName; config.ApiKeyValue = saved.ApiKeyValue; config.ApiKeyPlacement = saved.ApiKeyPlacement;
            config.JwtAlg = saved.JwtAlg; config.JwtSigningKey = saved.JwtSigningKey; config.JwtHeaders = saved.JwtHeaders;
            config.JwtClaims = saved.JwtClaims; config.JwtUseIat = saved.JwtUseIat; config.JwtExp = saved.JwtExp;
            config.OAuthGrantType = saved.OAuthGrantType; config.OAuthTokenEndpoint = saved.OAuthTokenEndpoint;
            config.OAuthClientId = saved.OAuthClientId; config.OAuthClientSecret = saved.OAuthClientSecret;
            config.OAuthScope = saved.OAuthScope; config.OAuthClientAuth = saved.OAuthClientAuth; config.OAuthAccessToken = saved.OAuthAccessToken;
        }
        if (config.IsPowerBase && string.IsNullOrWhiteSpace(config.ConnectionPublicId ?? config.Connection))
            throw new PipelineNonRetryableException("PowerBase requests require a connected account.");
        // Redirects must never forward connection credentials to another endpoint.
        using var client = _httpClientFactory.CreateClient("PipelineMakeRequest");
        client.Timeout = TimeSpan.FromMinutes(5);
        var executor = new MakeRequestExecutor((request, token) => config.IsPowerBase
            ? services.GetRequiredService<IPipelineApiRequestDispatcher>().SendAsync(request, services, token)
            : client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token));
        stepRun.InputContext = SerializeAndSanitizeAudit(new { RequestMode = config.RequestMode ?? "http", Method = config.Method,
            Url = config.IsPowerBase ? "PowerBase API" : new Uri(MakeRequestExecutor.BuildUrl(config, Resolve).GetLeftPart(UriPartial.Path)).ToString() });
        MakeRequestResult result;
        try { result = await executor.ExecuteAsync(config, Resolve, ct, audit => stepRun.InputContext = SerializeAndSanitizeAudit(audit, int.MaxValue)); }
        catch (InvalidOperationException ex) { throw new PipelineNonRetryableException(ex.Message); }
        catch (UnauthorizedAccessException ex) { throw new PipelineNonRetryableException(ex.Message); }
        if (!context.TryGetValue("request_metadata", out var metadata) || metadata is not Dictionary<string, object> requests)
            context["request_metadata"] = requests = new Dictionary<string, object>();
        requests[step.RefId] = new { status_code = result.StatusCode, status_message = result.StatusMessage, response_headers = result.ResponseHeaders };
        return result.OutputJson;
    }

    private static byte[] ComputeSha256Hash(string rawData)
    {
        using (var sha256 = System.Security.Cryptography.SHA256.Create())
        {
            return sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rawData));
        }
    }

    private static bool IsUniqueConstraintViolation(Exception ex)
    {
        var property = ex.GetType().GetProperty("Number");
        if (property != null)
        {
            var number = property.GetValue(ex);
            if (number is int intVal && (intVal == 2627 || intVal == 2601))
            {
                return true;
            }
        }
        if (ex.InnerException != null)
        {
            return IsUniqueConstraintViolation(ex.InnerException);
        }
        return false;
    }

    private async Task<string> ExecuteStepWithServicesAsync(
        PipelineStep step,
        string payloadJson,
        Dictionary<string, object> contextDict,
        List<PipelineStep> allSteps,
        Dictionary<string, object> stepsDict,
        long runId,
        PipelineStepRun stepRun,
        List<RawStepAuditSnapshot> snapshots,
        string executionPath,
        IRecordRepository recordRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordWriteService recordWriteService,
        IPipelineTriggerInterceptor triggerInterceptor,
        ITenantUnitOfWork uow,
        IPipelineStepIdempotencyRepository idempotencyRepo,
        IFileStorageService fileStorageService,
        IPipelineRecordSearchService recordSearchService,
        CancellationToken ct)
    {
        var subtype = step.Subtype?.ToLowerInvariant();
        var executionPathHash = ComputeSha256Hash(executionPath);
        Guid messageGuid = contextDict.TryGetValue("_MessageId", out var msgObj) && msgObj is Guid msgGuid ? msgGuid : Guid.Empty;
        long createdBy = contextDict.TryGetValue("_CreatedBy", out var cbObj) && cbObj is long cbVal ? cbVal : 0L;

        if (subtype == "create-record" || subtype == "update-record" || subtype == "delete-record" || subtype == "commit-upsert" || subtype == "upload-file")
        {
            var cachedOutput = await idempotencyRepo.GetByExecutionKeyAsync(messageGuid, step.PublicId, executionPathHash, null, ct);
            if (!string.IsNullOrEmpty(cachedOutput))
            {
                _logger.LogInformation("Idempotent replay match found for step {StepPublicId} at path {Path}. Returning cached output.", step.PublicId, executionPath);
                return cachedOutput;
            }
        }
        else if (subtype == "pause")
        {
            var cachedOutput = await idempotencyRepo.GetByExecutionKeyAsync(messageGuid, step.PublicId, executionPathHash, null, ct);
            if (!string.IsNullOrEmpty(cachedOutput))
            {
                var cached = JsonSerializer.Deserialize<Dictionary<string, object>>(cachedOutput);
                if (cached != null && cached.TryGetValue("ResumeDate", out var rdObj) && DateTime.TryParse(rdObj.ToString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var resumeDate))
                {
                    if (DateTime.UtcNow >= resumeDate)
                    {
                        _logger.LogInformation("Pause step {StepPublicId} wait condition satisfied.", step.PublicId);
                        return JsonSerializer.Serialize(new { Status = "Success" });
                    }
                    else
                    {
                        _logger.LogInformation("Pause step {StepPublicId} still waiting until {ResumeDate}.", step.PublicId, resumeDate);
                        throw new PowerBase.Domain.Exceptions.PipelineWaitException(resumeDate);
                    }
                }
                throw new InvalidOperationException("Saved pause resume date is invalid; refusing to restart the timer.");
            }

            var delay = PowerBase.Application.Common.Models.PauseStepConfig.ParseDuration(step.ConfigJson);
            var calculatedResumeDate = DateTime.UtcNow.Add(delay);

            var outputJson = JsonSerializer.Serialize(new { Status = "Waiting", ResumeDate = calculatedResumeDate });
            stepRun.InputContext = JsonSerializer.Serialize(new { DurationSeconds = delay.TotalSeconds });

            await idempotencyRepo.InsertAsync(new PipelineStepIdempotencyLog
            {
                MessageId = messageGuid,
                StepPublicId = step.PublicId,
                ExecutionPathHash = executionPathHash,
                ExecutionPath = executionPath,
                OutputJson = outputJson
            }, null, ct);

            throw new PowerBase.Domain.Exceptions.PipelineWaitException(calculatedResumeDate);
        }

        if (subtype == "call-another-pipeline")
        {
            var parentId = messageGuid == Guid.Empty
                ? CallablePipelineDispatcher.CreateMessageId(Guid.Empty, step.PublicId, runId.ToString(), Guid.Empty) : messageGuid;
            var priorDispatch = await idempotencyRepo.GetByExecutionKeyAsync(parentId, step.PublicId, executionPathHash, null, ct);
            if (!string.IsNullOrEmpty(priorDispatch)) return priorDispatch;
            var definition = CallablePipelineDefinition.ValidateConfig(step.ConfigJson, true);
            using var config = JsonDocument.Parse(step.ConfigJson!);
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var name in definition.Arguments)
            {
                var value = config.RootElement.GetProperty("arguments").GetProperty(name);
                values[name] = value.ValueKind == JsonValueKind.String
                    ? ResolveCallableValue(value.GetString(), payloadJson, executionPath, allSteps)
                    : ConvertJsonElement(value);
            }
            var dispatcher = new CallablePipelineDispatcher(_pipelineRepo, _serviceProvider.GetRequiredService<IPipelineExecutionQueue>());
            var messages = await dispatcher.DispatchAsync(_queryContext.TenantId, createdBy, step.PipelineId,
                parentId, step.PublicId, executionPath, contextDict.GetValueOrDefault("_CorrelationId")?.ToString(),
                contextDict.GetValueOrDefault("_Depth") is int depth ? depth : 1, definition, values, ct, _queryContext.PipelineChainJson);
            stepRun.InputContext = JsonSerializer.Serialize(new { definition.Definition, Arguments = values });
            var output = JsonSerializer.Serialize(new { Status = "Queued", MessageIds = messages });
            try
            {
                await idempotencyRepo.InsertAsync(new PipelineStepIdempotencyLog
                {
                    MessageId = parentId, StepPublicId = step.PublicId, ExecutionPathHash = executionPathHash,
                    ExecutionPath = executionPath, OutputJson = output
                }, null, ct);
            }
            catch (Exception ex) when (IsUniqueConstraintViolation(ex))
            {
                var winningOutput = await idempotencyRepo.GetByExecutionKeyAsync(parentId, step.PublicId, executionPathHash, null, ct);
                if (winningOutput != null) return winningOutput;
                throw;
            }
            return output;
        }
        else if (subtype == "pipeline-called" && step.Type == "trigger")
        {
            var definition = CallablePipelineDefinition.ValidateConfig(step.ConfigJson, false);
            using var payload = JsonDocument.Parse(payloadJson);
            if (!payload.RootElement.TryGetProperty("trigger", out var envelope) ||
                !envelope.TryGetProperty("CallDefinition", out var receivedDefinition) || receivedDefinition.GetString() != definition.Definition ||
                !envelope.TryGetProperty("Arguments", out var args) || args.ValueKind != JsonValueKind.Object ||
                !envelope.TryGetProperty("TriggerStepId", out var triggerId) || triggerId.GetInt64() != step.Id)
                throw new PipelineNonRetryableException("Pipeline Called requires a matching callable invocation.");
            var received = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var name in definition.Arguments)
            {
                if (!args.TryGetProperty(name, out var value)) throw new PipelineNonRetryableException($"Missing call argument: {name}.");
                received[name] = ConvertJsonElement(value.Clone());
            }
            stepsDict[step.RefId] = received;
            contextDict["trigger"] = received;
            var output = JsonSerializer.Serialize(received);
            stepRun.InputContext = output;
            return output;
        }
        else if (subtype == "look-up-record")
        {
            var config = JsonSerializer.Deserialize<LookUpRecordStepConfig>(step.ConfigJson ?? "{}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (config == null || string.IsNullOrWhiteSpace(config.TablePublicId))
                throw new InvalidOperationException("Look up record step configuration is invalid or missing tablePublicId.");

            _logger.LogInformation("Look Up a Record step {StepId} started for Table {TableId}.", step.Id, config.TablePublicId);

            var tableGuid = Guid.Parse(config.TablePublicId);
            var table = await tableRepo.GetByPublicIdAsync(tableGuid, ct);
            var fields = await fieldRepo.ListByTableAsync(table.Id, ct);

            var evaluatedRecordIdVal = EvaluateTokens(config.RecordIdValue, payloadJson, executionPath, allSteps);
            if (string.IsNullOrWhiteSpace(evaluatedRecordIdVal))
            {
                throw new InvalidOperationException("Evaluated Record ID lookup value is empty.");
            }

            if (!long.TryParse(evaluatedRecordIdVal, out var recordId))
            {
                throw new InvalidOperationException($"Evaluated Record ID value '{evaluatedRecordIdVal}' is not a valid numeric identifier.");
            }

            stepRun.InputContext = SerializeAndSanitizeAudit(new {
                TablePublicId = config.TablePublicId,
                RecordId = recordId,
                SubsequentFields = config.SubsequentFields,
                CompareLocalTime = config.CompareLocalTime
            });

            var queryFields = fields.ToList();

            var recordsDict = await recordRepo.GetRowsByIdsAsync(table, queryFields, new[] { recordId }, ct);
            if (recordsDict == null || !recordsDict.TryGetValue(recordId, out var record))
            {
                throw new InvalidOperationException($"Record with ID {recordId} was not found in Table '{table.Name}'.");
            }

            _logger.LogInformation("Look Up a Record step {StepId} found record ID {RecordId}.", step.Id, recordId);

            var norm = new Dictionary<string, object?>();
            if (record.TryGetValue("Id", out var lookupIdVal)) norm["Id"] = lookupIdVal;
            if (record.TryGetValue("PublicId", out var lookupPubIdVal))
            {
                norm["PublicId"] = lookupPubIdVal;
                norm["RecordPublicId"] = lookupPubIdVal;
            }
            if (record.TryGetValue("CreatedOn", out var lookupCoVal)) norm["CreatedOn"] = lookupCoVal;
            if (record.TryGetValue("CreatedBy", out var lookupCbVal)) norm["CreatedBy"] = lookupCbVal;
            if (record.TryGetValue("ModifiedOn", out var lookupMoVal)) norm["ModifiedOn"] = lookupMoVal;
            if (record.TryGetValue("ModifiedBy", out var lookupMbVal)) norm["ModifiedBy"] = lookupMbVal;

            foreach (var f in fields)
            {
                if (f.Fid.HasValue)
                {
                    var colName = PowerBase.Domain.Constants.PhysicalNaming.GetPhysicalColumnName(f);
                    if (record.TryGetValue(colName, out var val))
                    {
                        norm[$"fid_{f.Fid.Value}"] = val;
                    }
                    else if (record.TryGetValue(f.Name, out var valByName))
                    {
                        norm[$"fid_{f.Fid.Value}"] = valByName;
                    }
                }
            }

            return JsonSerializer.Serialize(norm);
        }

        if (subtype == "search-records")
        {
            var config = JsonSerializer.Deserialize<SearchRecordsStepConfig>(step.ConfigJson ?? "{}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (config == null || string.IsNullOrWhiteSpace(config.TableId))
                throw new InvalidOperationException("Search records step configuration is invalid or missing tableId.");

            _logger.LogInformation("Search Records step {StepId} started for Table {TableId}.", step.Id, config.TableId);

            var tableGuid = Guid.Parse(config.TableId);
            var table = await tableRepo.GetByPublicIdAsync(tableGuid, ct);
            var fields = await fieldRepo.ListByTableAsync(table.Id, ct);

            FilterGroup? filterTree = null;
            string? evaluatedFilterVal = null;

            if (config.FilterGroups != null && config.FilterGroups.Any(g => !PipelineFilterEvaluator.IsGroupCompletelyBlank(g)))
            {
                var outerGroup = new FilterGroup { Logic = "or", Nodes = new List<FilterNode>() };
                foreach (var g in config.FilterGroups)
                {
                    var mapped = MapTriggerFilterGroupToDbFilterGroup(g, fields, payloadJson, executionPath, allSteps);
                    if (mapped != null)
                    {
                        outerGroup.Nodes.Add(new FilterNode { Group = mapped });
                    }
                }
                if (outerGroup.Nodes.Any())
                {
                    filterTree = outerGroup.Nodes.Count == 1 ? outerGroup.Nodes[0].Group : outerGroup;
                }
            }
            else if (config.Filters != null && config.Filters.Any(r => !PipelineFilterEvaluator.IsRuleCompletelyBlank(r)))
            {
                var mockGroup = new TriggerFilterGroup { LogicalOp = "AND", Rules = config.Filters };
                filterTree = MapTriggerFilterGroupToDbFilterGroup(mockGroup, fields, payloadJson, executionPath, allSteps);
            }
            else if (!string.IsNullOrWhiteSpace(config.FilterField))
            {
                var field = ResolvePipelineField(config.FilterField, fields);

                if (field.Fid.HasValue)
                {
                    evaluatedFilterVal = EvaluateTokens(config.FilterValue, payloadJson, executionPath, allSteps);
                    var filterCondition = new FilterCondition
                    {
                        FieldId = field.Fid.Value,
                        Operator = "eq",
                        Value = evaluatedFilterVal
                    };
                    filterTree = new FilterGroup
                    {
                        Logic = "and",
                        Nodes = new List<FilterNode> { new FilterNode { Condition = filterCondition } }
                    };
                }
            }

            int? limit = config.MaxResults;
            var limitModeStr = limit.HasValue ? $"MaxResults={limit.Value}" : "LimitMode=Unlimited";
            _logger.LogInformation("Search Records step {StepId} started. {LimitMode}", step.Id, limitModeStr);

            stepRun.InputContext = SerializeAndSanitizeAudit(new {
                TableId = config.TableId,
                FilterField = config.FilterField,
                FilterValue = evaluatedFilterVal,
                FilterGroupsCount = config.FilterGroups?.Count ?? 0,
                FiltersCount = config.Filters?.Count ?? 0,
                MaxResults = limit
            });

            var records = recordSearchService != null
                ? await recordSearchService.SearchAsync(table, fields, maxResults: limit, filterTree: filterTree, ct: ct)
                : await recordRepo.ListAsync(table, fields, page: 1, pageSize: limit ?? 100000, filterTree: filterTree, ct: ct);
            var resultsList = records?.ToList() ?? new List<IReadOnlyDictionary<string, object?>>();
            _logger.LogInformation("Search Records step {StepId} matched {Count} records.", step.Id, resultsList.Count);

            var normalizedResults = new List<Dictionary<string, object?>>();
            foreach (var record in resultsList)
            {
                var norm = new Dictionary<string, object?>();
                if (record.TryGetValue("Id", out var searchIdVal)) norm["Id"] = searchIdVal;
                if (record.TryGetValue("PublicId", out var searchPubIdVal))
                {
                    norm["PublicId"] = searchPubIdVal;
                    norm["RecordPublicId"] = searchPubIdVal;
                }
                if (record.TryGetValue("CreatedOn", out var searchCoVal)) norm["CreatedOn"] = searchCoVal;
                if (record.TryGetValue("CreatedBy", out var searchCbVal)) norm["CreatedBy"] = searchCbVal;
                if (record.TryGetValue("ModifiedOn", out var searchMoVal)) norm["ModifiedOn"] = searchMoVal;
                if (record.TryGetValue("ModifiedBy", out var searchMbVal)) norm["ModifiedBy"] = searchMbVal;

                foreach (var f in fields)
                {
                    if (f.Fid.HasValue)
                    {
                        var colKey = PowerBase.Domain.Constants.PhysicalNaming.GetPhysicalColumnName(f);
                        if (record.TryGetValue(colKey, out var val))
                        {
                            norm[$"fid_{f.Fid.Value}"] = val;
                        }
                        else if (record.TryGetValue(f.Name, out var valByName))
                        {
                            norm[$"fid_{f.Fid.Value}"] = valByName;
                        }
                    }
                }
                normalizedResults.Add(norm);
            }

            var stepOutput = new { records = normalizedResults };
            return JsonSerializer.Serialize(stepOutput);
        }
        else if (subtype == "create-record")
        {
            var config = JsonSerializer.Deserialize<CreateRecordStepConfig>(step.ConfigJson ?? "{}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (config == null || string.IsNullOrWhiteSpace(config.TableId))
                throw new InvalidOperationException("Create record step configuration is invalid or missing tableId.");

            _logger.LogInformation("Create Record step {StepId} started for Table {TableId}.", step.Id, config.TableId);

            var tableGuid = Guid.Parse(config.TableId);
            var table = await tableRepo.GetByPublicIdAsync(tableGuid, ct);
            var fields = await fieldRepo.ListByTableAsync(table.Id, ct);

            var values = new Dictionary<long, object?>();
            var resolvedMappings = new Dictionary<string, object?>();
            if (config.FieldMappings != null)
            {
                foreach (var mapping in config.FieldMappings)
                {
                    if (string.IsNullOrWhiteSpace(mapping.Field)) continue;

                    var field = ResolvePipelineField(mapping.Field, fields);

                    if (field.Fid.HasValue)
                    {
                        var resolvedValStr = EvaluateTokens(mapping.Value, payloadJson, executionPath, allSteps);
                        // Quickbase single-record steps omit blank mappings instead of
                        // writing NULL (which would clear an existing value on update).
                        if (string.IsNullOrWhiteSpace(resolvedValStr)) continue;
                        var parsedVal = ParseRecordMappingValue(resolvedValStr, field, mapping.Value);
                        values[field.Fid.Value] = parsedVal;
                        resolvedMappings[mapping.Field] = parsedVal;
                    }
                }
            }

            stepRun.InputContext = SerializeAndSanitizeAudit(new {
                TableId = config.TableId,
                FieldMappings = resolvedMappings
            });

            Guid recordPublicId;
            await uow.BeginAsync(ct);
            try
            {
                var cachedOutput = await idempotencyRepo.GetByExecutionKeyAsync(messageGuid, step.PublicId, executionPathHash, uow.Transaction, ct);
                if (!string.IsNullOrEmpty(cachedOutput))
                {
                    await uow.CommitAsync(ct);
                    return cachedOutput;
                }

                recordPublicId = await recordRepo.CreateAsync(table, fields, values, uow.Transaction, ct);
                _logger.LogInformation("Create Record step {StepId} succeeded. Created record: {RecordPublicId}.", step.Id, recordPublicId);
                var recordId = await recordRepo.GetActiveRecordIdByPublicIdAsync(table, recordPublicId, uow.Transaction, ct);
                values[3] = recordId;
                await triggerInterceptor.InterceptAsync(table, fields, recordPublicId, values, "record-added", ct);

                var outputJson = JsonSerializer.Serialize(new { CreatedRecordPublicId = recordPublicId.ToString() });

                await idempotencyRepo.InsertAsync(new PipelineStepIdempotencyLog
                {
                    MessageId = messageGuid,
                    StepPublicId = step.PublicId,
                    ExecutionPathHash = executionPathHash,
                    ExecutionPath = executionPath,
                    OutputJson = outputJson
                }, uow.Transaction, ct);

                await uow.CommitAsync(ct);
                return outputJson;
            }
            catch (Exception ex) when (IsUniqueConstraintViolation(ex))
            {
                _logger.LogWarning(ex, "Create Record step {StepId} encountered unique constraint violation. Handling idempotency replay.", step.Id);
                await uow.RollbackAsync(CancellationToken.None);
                var winningOutput = await idempotencyRepo.GetByExecutionKeyAsync(messageGuid, step.PublicId, executionPathHash, null, ct);
                if (winningOutput != null) return winningOutput;
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Create Record step {StepId} failed.", step.Id);
                await uow.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        else if (subtype == "update-record")
        {
            var config = JsonSerializer.Deserialize<UpdateRecordStepConfig>(step.ConfigJson ?? "{}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (config == null || string.IsNullOrWhiteSpace(config.TableId))
                throw new InvalidOperationException("Update record step configuration is invalid or missing tableId.");

            var resolvedRecordIdStr = EvaluateTokens(config.TargetRecordId, payloadJson, executionPath, allSteps);
            if (!Guid.TryParse(resolvedRecordIdStr, out var recordPublicId))
                throw new InvalidOperationException($"Failed to resolve target record public ID from: '{config.TargetRecordId}'");

            var tableGuid = Guid.Parse(config.TableId);
            var table = await tableRepo.GetByPublicIdAsync(tableGuid, ct);
            var fields = await fieldRepo.ListByTableAsync(table.Id, ct);

            var values = new Dictionary<long, object?>();
            var resolvedMappings = new Dictionary<string, object?>();
            if (config.FieldMappings != null)
            {
                foreach (var mapping in config.FieldMappings)
                {
                    if (string.IsNullOrWhiteSpace(mapping.Field)) continue;

                    var field = ResolvePipelineField(mapping.Field, fields);

                    if (field.Fid.HasValue)
                    {
                        var resolvedValStr = EvaluateTokens(mapping.Value, payloadJson, executionPath, allSteps);
                        if (string.IsNullOrWhiteSpace(resolvedValStr)) continue;
                        var parsedVal = ParseRecordMappingValue(resolvedValStr, field, mapping.Value);
                        values[field.Fid.Value] = parsedVal;
                        resolvedMappings[mapping.Field] = parsedVal;
                    }
                }
            }

            stepRun.InputContext = SerializeAndSanitizeAudit(new {
                TableId = config.TableId,
                TargetRecordId = resolvedRecordIdStr,
                FieldMappings = resolvedMappings
            });

            await uow.BeginAsync(ct);
            try
            {
                var cachedOutput = await idempotencyRepo.GetByExecutionKeyAsync(messageGuid, step.PublicId, executionPathHash, uow.Transaction, ct);
                if (!string.IsNullOrEmpty(cachedOutput))
                {
                    await uow.CommitAsync(ct);
                    return cachedOutput;
                }

                var persisted = await recordWriteService.ApplyAsync(
                    table, fields, recordPublicId, values, AuditActions.Updated, "Record updated via Pipeline action step", ct, uow.Transaction);
                var outputJson = JsonSerializer.Serialize(new { UpdatedRecordPublicId = recordPublicId.ToString(), FieldCount = persisted.Count });

                await idempotencyRepo.InsertAsync(new PipelineStepIdempotencyLog
                {
                    MessageId = messageGuid,
                    StepPublicId = step.PublicId,
                    ExecutionPathHash = executionPathHash,
                    ExecutionPath = executionPath,
                    OutputJson = outputJson
                }, uow.Transaction, ct);

                await uow.CommitAsync(ct);
                return outputJson;
            }
            catch (Exception ex) when (IsUniqueConstraintViolation(ex))
            {
                await uow.RollbackAsync(CancellationToken.None);
                var winningOutput = await idempotencyRepo.GetByExecutionKeyAsync(messageGuid, step.PublicId, executionPathHash, null, ct);
                if (winningOutput != null) return winningOutput;
                throw;
            }
            catch
            {
                await uow.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        else if (subtype == "delete-record")
        {
            var config = JsonSerializer.Deserialize<DeleteRecordStepConfig>(step.ConfigJson ?? "{}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (config == null || string.IsNullOrWhiteSpace(config.TableId) || string.IsNullOrWhiteSpace(config.TargetRecordId))
                throw new InvalidOperationException("Delete record step configuration is invalid or missing tableId/targetRecordId.");

            var resolvedRecordIdStr = EvaluateTokens(config.TargetRecordId, payloadJson, executionPath, allSteps);
            if (!Guid.TryParse(resolvedRecordIdStr, out var recordPublicId))
                throw new InvalidOperationException($"Failed to resolve target record public ID from: '{config.TargetRecordId}'");

            stepRun.InputContext = SerializeAndSanitizeAudit(new {
                TableId = config.TableId,
                TargetRecordId = resolvedRecordIdStr
            });

            var tableGuid = Guid.Parse(config.TableId);
            var table = await tableRepo.GetByPublicIdAsync(tableGuid, ct);
            var fields = await fieldRepo.ListByTableAsync(table.Id, ct);

            var oldRecord = await recordRepo.GetByPublicIdAsync(table, fields, recordPublicId, ct);
            var oldValuesDict = new Dictionary<long, object?>();
            foreach (var field in fields)
            {
                if (field.Fid.HasValue)
                {
                    var colKey = PowerBase.Domain.Constants.PhysicalNaming.GetPhysicalColumnName(field);
                    if (oldRecord.TryGetValue(colKey, out var val))
                    {
                        oldValuesDict[field.Fid.Value] = val;
                    }
                }
            }

            await uow.BeginAsync(ct);
            try
            {
                var cachedOutput = await idempotencyRepo.GetByExecutionKeyAsync(messageGuid, step.PublicId, executionPathHash, uow.Transaction, ct);
                if (!string.IsNullOrEmpty(cachedOutput))
                {
                    await uow.CommitAsync(ct);
                    return cachedOutput;
                }

                await triggerInterceptor.InterceptAsync(table, fields, recordPublicId, oldValuesDict, "record-deleted", ct);
                await recordRepo.DeleteAsync(table, recordPublicId, uow.Transaction, ct);

                var outputJson = JsonSerializer.Serialize(new { DeletedRecordPublicId = recordPublicId.ToString() });

                await idempotencyRepo.InsertAsync(new PipelineStepIdempotencyLog
                {
                    MessageId = messageGuid,
                    StepPublicId = step.PublicId,
                    ExecutionPathHash = executionPathHash,
                    ExecutionPath = executionPath,
                    OutputJson = outputJson
                }, uow.Transaction, ct);

                await uow.CommitAsync(ct);
                return outputJson;
            }
            catch (Exception ex) when (IsUniqueConstraintViolation(ex))
            {
                await uow.RollbackAsync(CancellationToken.None);
                var winningOutput = await idempotencyRepo.GetByExecutionKeyAsync(messageGuid, step.PublicId, executionPathHash, null, ct);
                if (winningOutput != null) return winningOutput;
                throw;
            }
            catch
            {
                await uow.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        else if (subtype == "stop")
        {
            var config = JsonSerializer.Deserialize<StopStepConfig>(step.ConfigJson ?? "{}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var reason = EvaluateTokens(config?.Reason, payloadJson, executionPath, allSteps);

            stepRun.InputContext = SerializeAndSanitizeAudit(new {
                Reason = reason
            });

            throw new PipelineStopExecutionException(string.IsNullOrWhiteSpace(reason) ? "Execution halted by pipeline stop action." : reason);
        }
        else if (string.Equals(step.Type, "condition", StringComparison.OrdinalIgnoreCase) || string.Equals(step.Subtype, "condition", StringComparison.OrdinalIgnoreCase))
        {
            var config = JsonSerializer.Deserialize<ConditionStepConfig>(step.ConfigJson ?? "{}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            
            bool isMatched = false;
            var resolvedAuditRules = new List<object>();

            var activeGroups = config?.RuleGroups?.Where(g => !IsGroupCompletelyBlank(g)).ToList();
            if (activeGroups == null || !activeGroups.Any())
            {
                if (!string.IsNullOrWhiteSpace(config?.LeftOperand))
                {
                    var left = EvaluateTokens(config.LeftOperand, payloadJson, executionPath, allSteps);
                    var right = EvaluateTokens(config.RightOperand, payloadJson, executionPath, allSteps);
                    var op = config.Operator ?? "equals";
                    var typeCategory = await ResolveRuleTypeCategoryAsync(config.LeftOperand, payloadJson, allSteps, fieldRepo, tableRepo, ct);
                    isMatched = PipelineFilterEvaluator.EvaluateConditionOperator(left, op, right, typeCategory, _logger);
                    _logger.LogInformation("Condition step {StepId} resolved Left: '{Left}', Operator: '{Op}', Right: '{Right}'. Result: {Result}", step.Id, left, op, right, isMatched);

                    stepRun.InputContext = SerializeAndSanitizeAudit(new {
                        LeftOperand = config.LeftOperand,
                        LeftResolved = left,
                        Operator = op,
                        RightOperand = config.RightOperand,
                        RightResolved = right,
                        TypeCategory = typeCategory,
                        Matched = isMatched
                    });
                }
                else
                {
                    // Fail closed: no rule groups or all blank groups evaluate to false
                    isMatched = false;
                    _logger.LogInformation("Condition step {StepId} failed closed due to empty rule groups. Result: false", step.Id);
                    stepRun.InputContext = SerializeAndSanitizeAudit(new {
                        RuleGroups = new object[0],
                        Matched = false,
                        Reason = "No active condition rule groups configured"
                    });
                }
            }
            else
            {
                isMatched = await EvaluateConditionRuleGroupsAsync(activeGroups, payloadJson, executionPath, fieldRepo, tableRepo, allSteps, resolvedAuditRules, ct);
                _logger.LogInformation("Condition step {StepId} resolved via recursive ruleGroups evaluation. Result: {Result}", step.Id, isMatched);

                stepRun.InputContext = SerializeAndSanitizeAudit(new {
                    RuleGroups = activeGroups.Select(g => new {
                        g.LogicalOp,
                        Rules = g.Rules?.Select(r => new {
                            r.Type,
                            Left = r.Left,
                            LeftResolved = EvaluateTokens(r.Left, payloadJson, executionPath, allSteps),
                            Op = r.Op,
                            Right = r.Right,
                            RightResolved = EvaluateTokens(r.Right, payloadJson, executionPath, allSteps)
                        })
                    }),
                    ResolvedRules = resolvedAuditRules,
                    Matched = isMatched
                });
            }

            var branch = isMatched ? "children" : "elsechildren";
            await ExecuteSiblingStepsAsync(runId, allSteps, step.Id, branch, contextDict, stepsDict, snapshots, $"{executionPath}/{branch}", ct);

            return JsonSerializer.Serialize(new { Matched = isMatched, EvaluatedBranch = branch });
        }
        else if (subtype == "handle-errors")
        {
            var config = JsonSerializer.Deserialize<HandleErrorsStepConfig>(step.ConfigJson ?? "{}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var fallbackAction = (config?.FallbackAction?.ToLowerInvariant() == "ignore") ? "ignore" : "handle";

            stepRun.InputContext = SerializeAndSanitizeAudit(new {
                FallbackAction = fallbackAction
            });

            _logger.LogInformation("Handle Errors step {StepId} started monitoring. Mode: {FallbackAction}", step.Id, fallbackAction);

            bool monitoredSuccess = false;
            Exception? caughtException = null;

            try
            {
                await ExecuteSiblingStepsAsync(runId, allSteps, step.Id, "children", contextDict, stepsDict, snapshots, $"{executionPath}/monitored", ct);
                monitoredSuccess = true;
            }
            catch (Exception ex)
            {
                if (!IsCatchablePipelineStepError(ex))
                {
                    throw;
                }
                caughtException = ex;
                monitoredSuccess = false;
            }

            if (monitoredSuccess)
            {
                if (fallbackAction == "handle")
                {
                    await ExecuteSiblingStepsAsync(runId, allSteps, step.Id, "successchildren", contextDict, stepsDict, snapshots, $"{executionPath}/on_success", ct);
                }
                return JsonSerializer.Serialize(new { Handled = false, Status = "Success" });
            }
            else
            {
                if (fallbackAction == "ignore")
                {
                    _logger.LogWarning("Monitored step in Handle Errors step {StepId} failed with error '{ErrorMessage}'. Fallback action is Ignore; resuming pipeline.", step.Id, caughtException?.Message);
                    return JsonSerializer.Serialize(new { Handled = true, FallbackAction = "ignore", ErrorMessage = caughtException?.Message });
                }

                var failedSnap = snapshots.LastOrDefault(s => s.Status == "Failed");
                var failedRefId = failedSnap?.Step.RefId ?? step.RefId;
                var failedChannel = failedSnap?.Step.Type ?? "powerbase";
                var failedSubtype = failedSnap?.Step.Subtype ?? "action";
                var failedName = failedSnap?.Step.Label ?? "Action Step";
                var sanitizedMsg = SanitizeErrorMessage(caughtException?.Message ?? "Unknown error");

                var currentErrorDict = new Dictionary<string, object?>
                {
                    { "reference_id", failedRefId },
                    { "channel", failedChannel },
                    { "step", failedSubtype },
                    { "name", failedName },
                    { "error_message", sanitizedMsg }
                };

                object? prevStepsError = null;
                object? prevContextError = null;
                bool hadPrevStepsError = stepsDict.TryGetValue("ERROR", out prevStepsError);
                bool hadPrevContextError = contextDict.TryGetValue("ERROR", out prevContextError);

                try
                {
                    stepsDict["ERROR"] = currentErrorDict;
                    contextDict["ERROR"] = currentErrorDict;

                    _logger.LogInformation("Handle Errors step {StepId} executing On Error branch for failed step {FailedRefId}.", step.Id, failedRefId);

                    await ExecuteSiblingStepsAsync(runId, allSteps, step.Id, "errorchildren", contextDict, stepsDict, snapshots, $"{executionPath}/on_error", ct);
                }
                finally
                {
                    if (hadPrevStepsError) stepsDict["ERROR"] = prevStepsError!;
                    else stepsDict.Remove("ERROR");

                    if (hadPrevContextError) contextDict["ERROR"] = prevContextError!;
                    else contextDict.Remove("ERROR");
                }

                return JsonSerializer.Serialize(new { Handled = true, FallbackAction = "handle", FailedStepRefId = failedRefId, ErrorMessage = sanitizedMsg });
            }
        }
        else if (subtype == "loop" || subtype == "for-each")
        {
            var config = JsonSerializer.Deserialize<LoopStepConfig>(step.ConfigJson ?? "{}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (config == null || string.IsNullOrWhiteSpace(config.LoopOverStepId))
                throw new InvalidOperationException("Loop step configuration is invalid or missing LoopOverStepId.");

            var loopOverStep = allSteps.FirstOrDefault(s => s.RefId == config.LoopOverStepId && s.Type == "trigger" && s.Subtype == "new-bulk-event");
            if (loopOverStep != null)
            {
                if (messageGuid == Guid.Empty)
                    throw new InvalidOperationException("BulkEventId (MessageId) is missing from the execution context.");

                int pageSize = _options.BulkEventPageSize > 0 ? _options.BulkEventPageSize : 500;
                int totalCount = 0;
                if (contextDict.TryGetValue("trigger", out var triggerObj) && triggerObj is Dictionary<string, object> triggerDict && triggerDict.TryGetValue("count", out var countVal))
                {
                    totalCount = Convert.ToInt32(countVal);
                }

                stepRun.InputContext = SerializeAndSanitizeAudit(new {
                    LoopOverStepId = config.LoopOverStepId,
                    IsBulkEvent = true,
                    TotalCount = totalCount
                });

                _logger.LogInformation("Loop step {StepId} starting bulk event iteration. MessageId: {MessageId}, Total: {TotalCount}.", step.Id, messageGuid, totalCount);

                stepsDict.TryGetValue(step.RefId, out var previousLoopScope);
                int processedCount = 0;

                while (true)
                {
                    var pageRecords = await _pipelineRepo.GetPendingBulkEventRecordsPageAsync(messageGuid, 1, pageSize, ct);
                    if (pageRecords.Count == 0) break;

                    foreach (var r in pageRecords)
                    {
                        var recordObj = BuildBulkEventRecord(r);

                        var loopScope = new Dictionary<string, object>
                        {
                            { "item", recordObj },
                            { "index", r.Ordinal - 1 },
                            { "is_first", r.Ordinal == 1 },
                            { "is_last", r.Ordinal == totalCount }
                        };

                        stepsDict[step.RefId] = loopScope;

                        try
                        {
                            await ExecuteSiblingStepsAsync(runId, allSteps, step.Id, "children", contextDict, stepsDict, snapshots, $"{executionPath}/loop_index_{r.Ordinal}", ct);
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                // Mark item Processed = 2 (Failed) in database using source tenant repository / fresh connection
                                await _pipelineRepo.MarkBulkEventRecordsProcessedAsync(new List<long> { r.Id }, 2, transaction: null, ct);
                            }
                            catch (Exception markEx)
                            {
                                _logger.LogError(markEx, "Failed to update bulk event record status to Processed=2 for record {RecordId}", r.Id);
                            }

                            _logger.LogError(ex, "Iteration failed for Ordinal {Ordinal} in bulk event Loop step {StepId}", r.Ordinal, step.Id);
                            throw; // Re-throw to cause pipeline execution to enter crash retry state, resuming from failure item
                        }

                        // Mark item Processed = 1 (Success) in database using source tenant repository / fresh connection
                        await _pipelineRepo.MarkBulkEventRecordsProcessedAsync(new List<long> { r.Id }, 1, transaction: null, ct);
                        processedCount++;
                    }
                }

                if (previousLoopScope != null)
                {
                    stepsDict[step.RefId] = previousLoopScope;
                }
                else
                {
                    stepsDict.Remove(step.RefId);
                }

                return JsonSerializer.Serialize(new { LoopCompleted = true, IterationCount = processedCount, BulkLoop = true });
            }
            else
            {
                stepsDict.TryGetValue(config.LoopOverStepId, out var listObj);
                var items = GetLoopCollection(listObj);
                var itemsList = items?.ToList() ?? new List<object>();

                stepRun.InputContext = SerializeAndSanitizeAudit(new {
                    LoopOverStepId = config.LoopOverStepId,
                    ItemCount = itemsList.Count
                });

                _logger.LogInformation("Loop step {StepId} starting iteration. Total items to loop: {Count}.", step.Id, itemsList.Count);

                int index = 0;
                int count = itemsList.Count;
                int failedCount = 0;
                Exception? firstIterationError = null;

                stepsDict.TryGetValue(step.RefId, out var previousLoopScope);

                foreach (var item in itemsList)
                {
                    _logger.LogInformation("Loop step {StepId} iteration {Index} started.", step.Id, index);
                    var loopScope = new Dictionary<string, object>
                    {
                        { "item", item },
                        { "index", index },
                        { "is_first", index == 0 },
                        { "is_last", index == count - 1 }
                    };

                    stepsDict[step.RefId] = loopScope;

                    try
                    {
                        await ExecuteSiblingStepsAsync(runId, allSteps, step.Id, "children", contextDict, stepsDict, snapshots, $"{executionPath}/loop_index_{index}", ct);
                    }
                    catch (Exception ex)
                    {
                        if (IsControlFlowOrInfrastructureException(ex))
                        {
                            throw;
                        }
                        failedCount++;
                        firstIterationError ??= ex;
                        _logger.LogWarning(ex, "Iteration {Index} failed in Loop step {StepId}. Continuing remaining items; the loop will be marked failed.", index, step.Id);
                    }

                    _logger.LogInformation("Loop step {StepId} iteration {Index} completed.", step.Id, index);
                    index++;
                }

                if (previousLoopScope != null)
                {
                    stepsDict[step.RefId] = previousLoopScope;
                }
                else
                {
                    stepsDict.Remove(step.RefId);
                }

                if (failedCount > 0)
                {
                    //throw new InvalidOperationException($"Loop step '{step.RefId}' failed in {failedCount} of {count} iterations. First error: {firstIterationError?.Message}", firstIterationError);
                    _logger.LogWarning("Loop step '{StepRefId}' completed with {FailedCount} of {TotalCount} failed iterations.", step.RefId, failedCount, count);
                }

                return JsonSerializer.Serialize(new { LoopCompleted = true, IterationCount = count, FailedIterationCount = failedCount });
            }
        }
        else if (subtype == "send-email" || subtype == "send-email-outlook")
        {
            var config = JsonSerializer.Deserialize<SendEmailStepConfig>(step.ConfigJson ?? "{}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (config == null || string.IsNullOrWhiteSpace(config.ToAddresses))
                throw new InvalidOperationException("Send email step configuration is invalid or missing ToAddresses.");

            var resolvedTo = EvaluateTokens(config.ToAddresses, payloadJson, executionPath, allSteps);
            var resolvedSubject = EvaluateTokens(config.Subject, payloadJson, executionPath, allSteps);
            var resolvedBody = EvaluateTokens(config.Body, payloadJson, executionPath, allSteps);
            var resolvedCc = EvaluateTokens(config.CcAddresses, payloadJson, executionPath, allSteps);
            var resolvedBcc = EvaluateTokens(config.BccAddresses, payloadJson, executionPath, allSteps);
            var resolvedFrom = EvaluateTokens(config.FromAddress, payloadJson, executionPath, allSteps);

            List<string>? resolvedAttachments = null;
            if (config.Attachments != null && config.Attachments.Count > 0)
            {
                resolvedAttachments = new List<string>();
                foreach (var path in config.Attachments)
                {
                    var resolvedPath = EvaluateTokens(path, payloadJson, executionPath, allSteps);
                    if (!string.IsNullOrWhiteSpace(resolvedPath))
                    {
                        resolvedAttachments.Add(resolvedPath);
                    }
                }
            }

            stepRun.InputContext = SerializeAndSanitizeAudit(new {
                To = resolvedTo,
                Subject = resolvedSubject,
                Cc = resolvedCc,
                Bcc = resolvedBcc,
                From = resolvedFrom,
                Body = resolvedBody,
                Attachments = resolvedAttachments
            });

            await _emailService.SendEmailAsync(resolvedTo, resolvedSubject, resolvedBody, resolvedCc, resolvedBcc, resolvedAttachments, resolvedFrom, ct);

            return JsonSerializer.Serialize(new { SentTo = resolvedTo, Subject = resolvedSubject });
        }
        else if (subtype == "make-request")
        {
            return await ExecuteMakeRequestAsync(step, payloadJson, allSteps, contextDict, executionPath, stepRun, _serviceProvider, ct);
        }
        else if (subtype == "prepare-bulk-upsert")
        {
            var config = JsonSerializer.Deserialize<PrepareBulkUpsertConfig>(step.ConfigJson ?? "{}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var tableIdentifier = config?.GetTableIdentifier();
            var mergeKeyIdentifier = config?.GetMergeKeyIdentifier();

            if (string.IsNullOrWhiteSpace(tableIdentifier))
                throw new InvalidOperationException("Prepare bulk upsert step configuration is invalid or missing table.");

            if (string.IsNullOrWhiteSpace(mergeKeyIdentifier))
                mergeKeyIdentifier = "3";

            AppTable? table = null;
            if (Guid.TryParse(tableIdentifier, out var tableGuid))
            {
                table = await tableRepo.GetByPublicIdAsync(tableGuid, ct);
            }
            else if (long.TryParse(tableIdentifier, out var tableId))
            {
                table = await tableRepo.GetByIdAsync(tableId, ct);
            }
            else if (Guid.TryParse(tableIdentifier.Trim('"'), out var strippedGuid))
            {
                table = await tableRepo.GetByPublicIdAsync(strippedGuid, ct);
            }

            if (table == null)
                throw new InvalidOperationException($"Failed to resolve table: '{tableIdentifier}'");

            var fields = await fieldRepo.ListByTableAsync(table.Id, ct);
            var mergeField = ResolveBulkUpsertField(mergeKeyIdentifier, fields);

            if (mergeField == null || !mergeField.Fid.HasValue)
            {
                if (mergeKeyIdentifier == "3" || mergeKeyIdentifier.Equals("fid_3", StringComparison.OrdinalIgnoreCase) ||
                    mergeKeyIdentifier.Equals("Record ID", StringComparison.OrdinalIgnoreCase) ||
                    mergeKeyIdentifier.Equals("Record ID#", StringComparison.OrdinalIgnoreCase) ||
                    mergeKeyIdentifier.Equals("Id", StringComparison.OrdinalIgnoreCase))
                {
                    mergeField = fields.FirstOrDefault(f => f.Fid == 3) ?? new AppField { Fid = 3, Name = "Record ID#", TypeCode = "RecordId", IsSystem = true, PhysicalColumnName = "Id" };
                }
                else
                {
                    throw new InvalidOperationException($"Failed to resolve merge key field: '{mergeKeyIdentifier}'");
                }
            }

            stepRun.InputContext = SerializeAndSanitizeAudit(new {
                TableLabel = table.PublicId.ToString(),
                MergeKeyFid = mergeField.Fid.HasValue ? $"fid_{mergeField.Fid.Value}" : mergeKeyIdentifier
            });

            var session = new BulkUpsertSession
            {
                TableLabel = table.PublicId.ToString(),
                MergeKeyFid = mergeField.Fid.HasValue ? $"fid_{mergeField.Fid.Value}" : mergeKeyIdentifier,
                Table = table,
                Fields = fields,
                MergeField = mergeField,
                SelectedFieldFids = config?.Fields?.Select(f => ResolveBulkUpsertField(f, fields).Fid).Where(f => f.HasValue).Select(f => (long)f!.Value).ToHashSet(),
                Rows = new List<Dictionary<long, object?>>()
            };

            var sessions = GetOrCreateBulkUpsertSessions(contextDict);
            sessions[step.RefId] = session;
            if (step.Id > 0) sessions[step.Id.ToString()] = session;
            if (step.PublicId != Guid.Empty) sessions[step.PublicId.ToString()] = session;

            return JsonSerializer.Serialize(new { SessionId = step.RefId, Status = "Prepared" });
        }
        else if (subtype == "add-bulk-upsert-row")
        {
            var config = JsonSerializer.Deserialize<AddBulkUpsertRowConfig>(step.ConfigJson ?? "{}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var parentRefId = config?.GetParentRefId()?.Replace("steps.", "");
            if (string.IsNullOrWhiteSpace(parentRefId))
                throw new InvalidOperationException("Add bulk upsert row step configuration is invalid or missing ParentUpsertStepRefId.");

            var sessions = GetOrCreateBulkUpsertSessions(contextDict);
            BulkUpsertSession? parentSession = null;
            var parentStep = allSteps?.FirstOrDefault(s => s.RefId == parentRefId || s.Id.ToString() == parentRefId || s.PublicId.ToString() == parentRefId);

            if (!sessions.TryGetValue(parentRefId, out parentSession))
            {
                if (parentStep != null && sessions.TryGetValue(parentStep.RefId, out var foundSession))
                {
                    parentSession = foundSession;
                }
            }

            if (parentSession == null)
                throw new InvalidOperationException($"No bulk upsert session found for parent reference: '{parentRefId}'");

            bool isParentPrepare = parentStep != null ? parentStep.Subtype == "prepare-bulk-upsert" : (parentSession.RootSession == null && parentSession.IsCommittable);

            // Zero-query row add: use cached table and fields from parentSession or root
            var root = parentSession.RootSession ?? parentSession;
            var table = root.Table;
            var fields = root.Fields;
            if (table == null || fields == null)
            {
                var tableGuid = Guid.Parse(root.TableLabel);
                table = await tableRepo.GetByPublicIdAsync(tableGuid, ct);
                fields = await fieldRepo.ListByTableAsync(table.Id, ct);
                root.Table = table;
                root.Fields = fields;
            }

            var rowValues = new Dictionary<long, object?>();
            // A selected bulk-import column is present in every row, even when its mapping is blank.
            if (root.SelectedFieldFids != null)
            {
                foreach (var field in fields.Where(f => f.Fid.HasValue && root.SelectedFieldFids.Contains(f.Fid.Value) && !f.IsSystem))
                    rowValues[field.Fid!.Value] = ParseBulkUpsertValueType("", field.TypeCode);
            }

            var resolvedMappings = new Dictionary<string, object?>();

            // 1. Process FieldMappings if present
            if (config?.FieldMappings != null)
            {
                foreach (var mapping in config.FieldMappings)
                {
                    if (string.IsNullOrWhiteSpace(mapping.Field)) continue;
                    var field = ResolveBulkUpsertField(mapping.Field, fields);
                    if (field != null && field.Fid.HasValue)
                    {
                        var resolvedValStr = mapping.Value != null ? EvaluateTokens(mapping.Value, payloadJson, executionPath, allSteps) : null;
                        var parsedVal = mapping.Value != null ? ParseBulkUpsertValueType(resolvedValStr, field.TypeCode) : null;
                        rowValues[field.Fid.Value] = parsedVal;
                        resolvedMappings[mapping.Field] = parsedVal;
                    }
                }
            }

            // 2. Process RowValues if present (supports frontend contract)
            if (config?.RowValues != null)
            {
                foreach (var kvp in config.RowValues)
                {
                    if (string.IsNullOrWhiteSpace(kvp.Key)) continue;
                    var field = ResolveBulkUpsertField(kvp.Key, fields);
                    if (field != null && field.Fid.HasValue)
                    {
                        var rawVal = kvp.Value;
                        object? parsedVal;
                        if (rawVal == null)
                        {
                            parsedVal = null;
                        }
                        else if (rawVal is JsonElement el)
                        {
                            if (el.ValueKind == JsonValueKind.Null)
                            {
                                parsedVal = null;
                            }
                            else if (el.ValueKind == JsonValueKind.String)
                            {
                                var resolvedValStr = EvaluateTokens(el.GetString(), payloadJson, executionPath, allSteps);
                                parsedVal = ParseBulkUpsertValueType(resolvedValStr, field.TypeCode);
                            }
                            else
                            {
                                var resolvedValStr = EvaluateTokens(el.GetRawText(), payloadJson, executionPath, allSteps);
                                parsedVal = ParseBulkUpsertValueType(resolvedValStr, field.TypeCode);
                            }
                        }
                        else if (rawVal is string s)
                        {
                            var resolvedValStr = EvaluateTokens(s, payloadJson, executionPath, allSteps);
                            parsedVal = ParseBulkUpsertValueType(resolvedValStr, field.TypeCode);
                        }
                        else
                        {
                            parsedVal = rawVal;
                        }

                        if (field.Fid.Value != 3 || (parsedVal != null && long.TryParse(parsedVal.ToString(), out var checkId) && checkId > 0))
                        {
                            rowValues[field.Fid.Value] = parsedVal;
                            resolvedMappings[kvp.Key] = parsedVal;
                        }
                    }
                }
            }

            stepRun.InputContext = SerializeAndSanitizeAudit(new {
                ParentUpsertStepRefId = parentRefId,
                BulkRecordSetStepId = parentRefId,
                FieldMappings = resolvedMappings
            });

            if (isParentPrepare)
            {
                // Direct Add Row -> Prepare: contribute to root Prepare collection
                parentSession.Rows.Add(rowValues);

                var node = new BulkUpsertSession
                {
                    TableLabel = root.TableLabel,
                    MergeKeyFid = root.MergeKeyFid,
                    Table = root.Table,
                    Fields = root.Fields,
                    MergeField = root.MergeField,
                    RootSession = root,
                    IsCommittable = false
                };

                sessions[step.RefId] = node;
                if (step.Id > 0) sessions[step.Id.ToString()] = node;
                if (step.PublicId != Guid.Empty) sessions[step.PublicId.ToString()] = node;

                return JsonSerializer.Serialize(new { Status = "RowAdded", RowCount = parentSession.Rows.Count });
            }
            else
            {
                // Add Row -> Add Row (Chaining / Fan-Out): contribute to shared downstream child collection
                var targetCollection = parentSession.DownstreamChildCollection;
                if (targetCollection == null)
                {
                    targetCollection = new BulkUpsertSession
                    {
                        TableLabel = root.TableLabel,
                        MergeKeyFid = root.MergeKeyFid,
                        Table = root.Table,
                        Fields = root.Fields,
                        MergeField = root.MergeField,
                        RootSession = root,
                        IsCommittable = true,
                        Rows = new List<Dictionary<long, object?>>()
                    };
                    parentSession.DownstreamChildCollection = targetCollection;
                }

                targetCollection.Rows.Add(rowValues);

                var node = new BulkUpsertSession
                {
                    TableLabel = root.TableLabel,
                    MergeKeyFid = root.MergeKeyFid,
                    Table = root.Table,
                    Fields = root.Fields,
                    MergeField = root.MergeField,
                    RootSession = root,
                    DownstreamChildCollection = targetCollection,
                    IsCommittable = false
                };

                sessions[step.RefId] = node;
                if (step.Id > 0) sessions[step.Id.ToString()] = node;
                if (step.PublicId != Guid.Empty) sessions[step.PublicId.ToString()] = node;

                return JsonSerializer.Serialize(new { Status = "RowAdded", RowCount = targetCollection.Rows.Count });
            }
        }
        else if (subtype == "commit-upsert")
        {
            var config = JsonSerializer.Deserialize<CommitBulkUpsertConfig>(step.ConfigJson ?? "{}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var parentRefId = config?.GetParentRefId()?.Replace("steps.", "");
            if (string.IsNullOrWhiteSpace(parentRefId))
                throw new InvalidOperationException("Commit bulk upsert step configuration is invalid or missing ParentUpsertStepRefId.");

            stepRun.InputContext = SerializeAndSanitizeAudit(new {
                ParentUpsertStepRefId = parentRefId,
                BulkRecordSetStepId = parentRefId
            });

            var sessions = GetOrCreateBulkUpsertSessions(contextDict);
            BulkUpsertSession? session = null;
            string sessionKey = parentRefId;

            if (sessions.TryGetValue(parentRefId, out session))
            {
                sessionKey = parentRefId;
            }
            else
            {
                var refStep = allSteps?.FirstOrDefault(s => s.RefId == parentRefId || s.Id.ToString() == parentRefId || s.PublicId.ToString() == parentRefId);
                if (refStep != null && sessions.TryGetValue(refStep.RefId, out session))
                {
                    sessionKey = refStep.RefId;
                }
            }

            if (session == null)
                throw new InvalidOperationException($"No bulk upsert session found for parent reference: '{config?.GetParentRefId() ?? parentRefId}'");

            BulkUpsertSession? committableSession = null;
            if (session.DownstreamChildCollection != null)
            {
                committableSession = session.DownstreamChildCollection;
            }
            else if (session.IsCommittable)
            {
                committableSession = session;
            }

            if (committableSession == null)
            {
                // Direct Add Row -> Prepare with no downstream child: commits 0 records
                return JsonSerializer.Serialize(new
                {
                    Status = "Committed",
                    inserted_count = 0,
                    updated_count = 0,
                    unchanged_count = 0,
                    inserted_record_ids = Array.Empty<long>(),
                    updated_record_ids = Array.Empty<long>(),
                    total_rows = 0,
                    total_failed = 0
                });
            }

            session = committableSession;

            var table = session.Table;
            var fields = session.Fields;
            if (table == null || fields == null)
            {
                var tableGuid = Guid.Parse(session.TableLabel);
                table = await tableRepo.GetByPublicIdAsync(tableGuid, ct);
                fields = await fieldRepo.ListByTableAsync(table.Id, ct);
                session.Table = table;
                session.Fields = fields;
            }

            var mergeField = session.MergeField ?? ResolveBulkUpsertField(session.MergeKeyFid, fields);
            if (mergeField == null || !mergeField.Fid.HasValue)
            {
                if (session.MergeKeyFid == "3" || session.MergeKeyFid.Equals("fid_3", StringComparison.OrdinalIgnoreCase) ||
                    session.MergeKeyFid.Equals("Record ID", StringComparison.OrdinalIgnoreCase) ||
                    session.MergeKeyFid.Equals("Record ID#", StringComparison.OrdinalIgnoreCase) ||
                    session.MergeKeyFid.Equals("Id", StringComparison.OrdinalIgnoreCase))
                {
                    mergeField = fields.FirstOrDefault(f => f.Fid == 3) ?? new AppField { Fid = 3, Name = "Record ID#", TypeCode = "Numeric", IsSystem = true, PhysicalColumnName = "Id" };
                }
                else
                {
                    throw new InvalidOperationException($"Failed to resolve merge key field: '{session.MergeKeyFid}'");
                }
            }

            // MANDATORY CONDITION 3: STRICT IN-BATCH DUPLICATE DETECTION (In-Memory, BEFORE DB transaction)
            var seenMergeKeys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < session.Rows.Count; i++)
            {
                var r = session.Rows[i];
                if (r.TryGetValue(mergeField.Fid.Value, out var mVal) && mVal != null && !string.IsNullOrWhiteSpace(mVal.ToString()))
                {
                    var keyStr = mVal.ToString()!.Trim();
                    if (seenMergeKeys.TryGetValue(keyStr, out var firstIndex))
                    {
                        sessions.Remove(sessionKey);
                        throw new PipelineBulkUpsertException(
                            "DUPLICATE_MERGE_KEY_IN_BATCH",
                            $"Duplicate merge key '{keyStr}' found in batch at row {firstIndex + 1} and row {i + 1}.",
                            rowIndex: i + 1,
                            fieldFid: mergeField.Fid,
                            fieldName: !string.IsNullOrWhiteSpace(mergeField.Label) ? mergeField.Label : mergeField.Name
                        );
                    }
                    seenMergeKeys[keyStr] = i;
                }
            }

            int inserted = 0;
            int updated = 0;
            var createdRecordIds = new List<long>();
            var updatedRecordIds = new List<long>();
            var addedChanges = new List<PipelineRecordChange>();
            var modifiedChanges = new List<PipelineRecordChange>();

            // MANDATORY CONDITION 1: UNIFIED TRANSACTION CONNECTION
            await uow.BeginAsync(ct);
            try
            {
                var cachedOutput = await idempotencyRepo.GetByExecutionKeyAsync(messageGuid, step.PublicId, executionPathHash, uow.Transaction, ct);
                if (!string.IsNullOrEmpty(cachedOutput))
                {
                    await uow.CommitAsync(ct);
                    sessions.Remove(sessionKey);
                    return cachedOutput;
                }

                var isMergeRecordId = (mergeField.IsSystem && string.Equals(mergeField.PhysicalColumnName, "Id", StringComparison.OrdinalIgnoreCase)) ||
                                      (string.Equals(mergeField.TypeCode, "RecordId", StringComparison.OrdinalIgnoreCase));
                var mergeColName = isMergeRecordId ? "Id" : PhysicalNaming.GetPhysicalColumnName(mergeField);

                var candidateRecordIds = new HashSet<long>();
                var candidateMergeKeys = new HashSet<object>();

                foreach (var row in session.Rows)
                {
                    if (row.TryGetValue(3, out var idVal) && idVal != null && !string.IsNullOrWhiteSpace(idVal.ToString()))
                    {
                        if (long.TryParse(idVal.ToString(), out var parsedLongId) && parsedLongId > 0)
                        {
                            candidateRecordIds.Add(parsedLongId);
                        }
                    }

                    if (row.TryGetValue(mergeField.Fid.Value, out var mkVal) && mkVal != null && !string.IsNullOrWhiteSpace(mkVal.ToString()))
                    {
                        if (isMergeRecordId)
                        {
                            if (long.TryParse(mkVal.ToString(), out var parsedLongId) && parsedLongId > 0)
                            {
                                candidateRecordIds.Add(parsedLongId);
                            }
                        }
                        else
                        {
                            candidateMergeKeys.Add(mkVal);
                        }
                    }
                }

                // Set-based / chunked lookup on uow.Transaction with UPDLOCK/HOLDLOCK concurrency hints
                IReadOnlyDictionary<long, IReadOnlyDictionary<string, object?>> rowsById = new Dictionary<long, IReadOnlyDictionary<string, object?>>();
                if (candidateRecordIds.Count > 0)
                {
                    rowsById = await recordRepo.GetBulkUpsertRowsByIdsAsync(table, fields, candidateRecordIds.ToList(), uow.Transaction, ct);
                }

                IReadOnlyDictionary<object, IReadOnlyDictionary<string, object?>> rowsByMergeKey = new Dictionary<object, IReadOnlyDictionary<string, object?>>();
                if (candidateMergeKeys.Count > 0 && !isMergeRecordId)
                {
                    rowsByMergeKey = await recordRepo.GetBulkUpsertRowsByColumnValuesAsync(table, fields, mergeColName, candidateMergeKeys.ToList(), uow.Transaction, ct);
                }

                IReadOnlyDictionary<string, object?>? FindByMergeKey(object? mkVal)
                {
                    if (mkVal == null) return null;
                    var mkStr = mkVal.ToString();
                    if (string.IsNullOrWhiteSpace(mkStr)) return null;

                    if (isMergeRecordId)
                    {
                        if (long.TryParse(mkStr, out var parsedLongId) && rowsById.TryGetValue(parsedLongId, out var found))
                        {
                            return found;
                        }
                        return null;
                    }

                    if (rowsByMergeKey.TryGetValue(mkVal, out var directMatch))
                        return directMatch;

                    var trimmed = mkStr.Trim();
                    if (rowsByMergeKey.TryGetValue(trimmed, out var trimmedMatch))
                        return trimmedMatch;

                    return null;
                }

                var writableFids = fields
                    .Where(f => !f.IsSystem && f.Fid.HasValue && !PhysicalNaming.IsComputedTypeCode(f.TypeCode))
                    .Select(f => (long)f.Fid!.Value)
                    .ToHashSet();

                var addedRecords = new List<(Guid PublicId, Dictionary<long, object?> Row, Dictionary<long, object?> ChangeValues)>();

                // Process each row according to Plan V2 Cases 1–6
                for (int i = 0; i < session.Rows.Count; i++)
                {
                    var row = session.Rows[i];

                    long? suppliedRecordId = null;
                    if (row.TryGetValue(3, out var rIdObj) && rIdObj != null && !string.IsNullOrWhiteSpace(rIdObj.ToString()) && long.TryParse(rIdObj.ToString(), out var rid) && rid > 0)
                    {
                        suppliedRecordId = rid;
                    }

                    object? suppliedMergeKey = null;
                    if (row.TryGetValue(mergeField.Fid.Value, out var mKeyObj) && mKeyObj != null && !string.IsNullOrWhiteSpace(mKeyObj.ToString()))
                    {
                        suppliedMergeKey = mKeyObj;
                    }

                    bool recordIdProvided = suppliedRecordId.HasValue;
                    bool mergeKeyProvided = suppliedMergeKey != null;

                    IReadOnlyDictionary<string, object?>? recordA = null;
                    if (recordIdProvided)
                    {
                        rowsById.TryGetValue(suppliedRecordId!.Value, out recordA);
                    }

                    IReadOnlyDictionary<string, object?>? recordB = null;
                    if (mergeKeyProvided)
                    {
                        recordB = FindByMergeKey(suppliedMergeKey);
                    }

                    IReadOnlyDictionary<string, object?>? matchedExistingRow = null;

                    if (recordIdProvided && mergeKeyProvided)
                    {
                        // CASE 5: Record ID supplied but does not exist in DB
                        if (recordA == null)
                        {
                            sessions.Remove(sessionKey);
                            throw new PipelineBulkUpsertException(
                                "RECORD_ID_NOT_FOUND",
                                $"Record ID {suppliedRecordId!.Value} was not found in Table '{table.Name}'.",
                                rowIndex: i + 1,
                                fieldFid: 3,
                                fieldName: "Record ID#"
                            );
                        }

                        long idA = Convert.ToInt64(recordA["Id"]);

                        if (recordB != null)
                        {
                            long idB = Convert.ToInt64(recordB["Id"]);
                            if (idA == idB)
                            {
                                // CASE 3: Both Record ID and Merge Key identify the SAME record -> UPDATE
                                matchedExistingRow = recordA;
                            }
                            else
                            {
                                // CASE 4 / CASE 6 Collision: Record ID identifies Record A, Merge Key identifies Record B -> REJECT
                                sessions.Remove(sessionKey);
                                throw new PipelineBulkUpsertException(
                                    "RECORD_ID_MERGE_KEY_MISMATCH",
                                    $"Record ID {suppliedRecordId!.Value} (Record {idA}) and Merge Key '{suppliedMergeKey}' (Record {idB}) identify different records in Table '{table.Name}' at row {i + 1}.",
                                    rowIndex: i + 1,
                                    fieldFid: mergeField.Fid,
                                    fieldName: !string.IsNullOrWhiteSpace(mergeField.Label) ? mergeField.Label : mergeField.Name
                                );
                            }
                        }
                        else
                        {
                            // CASE 6: Record ID exists and incoming Merge Key is unused -> UPDATE Record A
                            matchedExistingRow = recordA;
                        }
                    }
                    else if (recordIdProvided && !mergeKeyProvided)
                    {
                        // CASE 1: Record ID only
                        if (recordA != null)
                        {
                            matchedExistingRow = recordA; // UPDATE
                        }
                        else
                        {
                            sessions.Remove(sessionKey);
                            throw new PipelineBulkUpsertException(
                                "RECORD_ID_NOT_FOUND",
                                $"Record ID {suppliedRecordId!.Value} was not found in Table '{table.Name}'.",
                                rowIndex: i + 1,
                                fieldFid: 3,
                                fieldName: "Record ID#"
                            );
                        }
                    }
                    else if (!recordIdProvided && mergeKeyProvided)
                    {
                        // CASE 2: Merge Key only
                        if (recordB != null)
                        {
                            matchedExistingRow = recordB; // UPDATE
                        }
                        else
                        {
                            matchedExistingRow = null; // INSERT
                        }
                    }
                    else
                    {
                        // Neither provided -> INSERT
                        matchedExistingRow = null;
                    }

                    // Remove non-writable (system / computed) fields from row before database writes
                    var nonWritableKeys = row.Keys.Where(k => !writableFids.Contains(k)).ToList();
                    foreach (var k in nonWritableKeys)
                    {
                        row.Remove(k);
                    }

                    if (matchedExistingRow != null)
                    {
                        // UPDATE (Cases 1, 2-update, 3, 6-update)
                        Guid? recordPublicId = null;
                        if (matchedExistingRow.TryGetValue("publicId", out var pubIdObj) && pubIdObj is Guid g1)
                        {
                            recordPublicId = g1;
                        }
                        else if (matchedExistingRow.TryGetValue("PublicId", out var pubIdObj2) && pubIdObj2 is Guid g2)
                        {
                            recordPublicId = g2;
                        }
                        else if (matchedExistingRow.TryGetValue("publicId", out var pStr) && Guid.TryParse(pStr?.ToString(), out var g3))
                        {
                            recordPublicId = g3;
                        }
                        else if (matchedExistingRow.TryGetValue("PublicId", out var pStr2) && Guid.TryParse(pStr2?.ToString(), out var g4))
                        {
                            recordPublicId = g4;
                        }

                        if (recordPublicId.HasValue)
                        {
                            var beforeValues = new Dictionary<long, object?>();
                            var afterValues = new Dictionary<long, object?>();
                            var changedFieldIds = new List<long>();

                            foreach (var f in fields)
                            {
                                if (f.Fid.HasValue)
                                {
                                    var colKey = PhysicalNaming.GetPhysicalColumnName(f);
                                    var oldVal = matchedExistingRow.TryGetValue(colKey, out var ov) ? ov : (matchedExistingRow.TryGetValue($"fid_{f.Fid.Value}", out var ov2) ? ov2 : null);
                                    beforeValues[f.Id] = oldVal;
                                    beforeValues[f.Fid.Value] = oldVal;

                                    if (row.ContainsKey(f.Fid.Value))
                                    {
                                        var newVal = row[f.Fid.Value];
                                        afterValues[f.Id] = newVal;
                                        afterValues[f.Fid.Value] = newVal;

                                        if (!f.IsSystem && !PhysicalNaming.IsComputedTypeCode(f.TypeCode) && !AreValuesEqual(oldVal, newVal, f.TypeCode))
                                        {
                                            changedFieldIds.Add((long)f.Fid.Value);
                                        }
                                    }
                                    else
                                    {
                                        afterValues[f.Id] = oldVal;
                                        afterValues[f.Fid.Value] = oldVal;
                                    }
                                }
                            }

                            // UPDATE in DB on uow.Transaction via recordWriteService (with sanitized row)
                            await recordWriteService.ApplyAsync(
                                table,
                                fields,
                                recordPublicId.Value,
                                row,
                                Domain.Constants.AuditActions.Updated,
                                $"Pipeline '{step.PipelineId}' step '{step.Id}' updated record {recordPublicId.Value}",
                                ct,
                                uow.Transaction,
                                suppressInterception: true,
                                existingRecord: matchedExistingRow
                            );

                            modifiedChanges.Add(new PipelineRecordChange(
                                recordPublicId.Value,
                                beforeValues,
                                afterValues,
                                changedFieldIds,
                                PipelineRecordEventType.Modified
                            ));

                            if (matchedExistingRow.TryGetValue("Id", out var exId) && exId != null && long.TryParse(exId.ToString(), out var parsedExId))
                            {
                                updatedRecordIds.Add(parsedExId);
                            }
                            else if (suppliedRecordId.HasValue)
                            {
                                updatedRecordIds.Add(suppliedRecordId.Value);
                            }

                            updated++;
                        }
                        else
                        {
                            var createdPublicId = await recordRepo.CreateAsync(table, fields, row, uow.Transaction, ct);
                            var changeValues = new Dictionary<long, object?>();
                            foreach (var f in fields)
                            {
                                if (f.Fid.HasValue && row.TryGetValue(f.Fid.Value, out var val))
                                {
                                    changeValues[f.Id] = val;
                                    changeValues[f.Fid.Value] = val;
                                }
                            }
                            addedRecords.Add((createdPublicId, row, changeValues));
                            inserted++;
                        }
                    }
                    else
                    {
                        // INSERT (Cases 2-insert, 6-insert)
                        var createdPublicId = await recordRepo.CreateAsync(table, fields, row, uow.Transaction, ct);
                        var changeValues = new Dictionary<long, object?>();
                        foreach (var f in fields)
                        {
                            if (f.Fid.HasValue && row.TryGetValue(f.Fid.Value, out var val))
                            {
                                changeValues[f.Id] = val;
                                changeValues[f.Fid.Value] = val;
                            }
                        }
                        addedRecords.Add((createdPublicId, row, changeValues));
                        inserted++;
                    }
                }

                if (addedRecords.Count > 0)
                {
                    var publicIds = addedRecords.Select(r => r.PublicId).ToList();
                    var publicIdToIdMap = await recordRepo.GetActiveRecordIdsByPublicIdsAsync(table, publicIds, uow.Transaction, ct);
                    var recordIdField = fields.FirstOrDefault(f => f.Fid == 3);

                    foreach (var (pubId, r, cv) in addedRecords)
                    {
                        if (publicIdToIdMap.TryGetValue(pubId, out var id))
                        {
                            r[3] = id;
                            if (recordIdField != null)
                            {
                                cv[recordIdField.Id] = id;
                            }
                            cv[3] = id;
                            createdRecordIds.Add(id);
                        }

                        addedChanges.Add(new PipelineRecordChange(
                            pubId,
                            new Dictionary<long, object?>(),
                            cv,
                            new List<long>(),
                            PipelineRecordEventType.Added
                        ));
                    }
                }

                var commitUpsertBatchId = Guid.NewGuid();
                var commitUpsertCorrelationId = Guid.NewGuid();
                if (addedChanges.Count > 0)
                {
                    await triggerInterceptor.InterceptBulkAsync(
                        table,
                        fields,
                        addedChanges,
                        commitUpsertBatchId,
                        commitUpsertCorrelationId,
                        createdBy,
                        ct
                    );
                }
                if (modifiedChanges.Count > 0)
                {
                    await triggerInterceptor.InterceptBulkAsync(
                        table,
                        fields,
                        modifiedChanges,
                        commitUpsertBatchId,
                        commitUpsertCorrelationId,
                        createdBy,
                        ct
                    );
                }

                var outputJson = JsonSerializer.Serialize(new
                {
                    committed = true,
                    status = "Committed",
                    inserted_count = inserted,
                    updated_count = updated,
                    total_records = inserted + updated,
                    created_record_ids = createdRecordIds,
                    updated_record_ids = updatedRecordIds,
                    errors = Array.Empty<object>(),
                    // CamelCase aliases for backwards compatibility
                    insertedCount = inserted,
                    updatedCount = updated,
                    unchangedCount = 0
                });

                await idempotencyRepo.InsertAsync(new PipelineStepIdempotencyLog
                {
                    MessageId = messageGuid,
                    StepPublicId = step.PublicId,
                    ExecutionPathHash = executionPathHash,
                    ExecutionPath = executionPath,
                    OutputJson = outputJson
                }, uow.Transaction, ct);

                await uow.CommitAsync(ct);
                sessions.Remove(sessionKey);
                return outputJson;
            }
            catch (Exception ex) when (IsUniqueConstraintViolation(ex))
            {
                await uow.RollbackAsync(CancellationToken.None);
                sessions.Remove(sessionKey);
                var winningOutput = await idempotencyRepo.GetByExecutionKeyAsync(messageGuid, step.PublicId, executionPathHash, null, ct);
                if (winningOutput != null) return winningOutput;
                throw;
            }
            catch
            {
                await uow.RollbackAsync(CancellationToken.None);
                sessions.Remove(sessionKey);
                throw;
            }
        }
        else if (subtype == "upload-file")
        {
            var config = JsonSerializer.Deserialize<UploadFileStepConfig>(step.ConfigJson ?? "{}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (config == null)
                throw new InvalidOperationException("Upload file step configuration is invalid.");
            var fileUrl = !string.IsNullOrWhiteSpace(config.FileUrl) ? config.FileUrl : config.FileSourceUrl;
            if (string.IsNullOrWhiteSpace(fileUrl))
                throw new InvalidOperationException("Upload file step configuration is missing FileUrl.");

            var resolvedUrl = EvaluateTokens(fileUrl, payloadJson, executionPath, allSteps);
            var resolvedFileName = EvaluateTokens(config.FileName, payloadJson, executionPath, allSteps);

            stepRun.InputContext = SerializeAndSanitizeAudit(new {
                FileUrl = resolvedUrl,
                FileName = resolvedFileName,
                FileRecordStepId = config.FileRecordStepId,
                TargetFileField = config.TargetFileField
            });

            if (string.IsNullOrWhiteSpace(resolvedFileName))
            {
                try
                {
                    resolvedFileName = System.IO.Path.GetFileName(new Uri(resolvedUrl).LocalPath);
                }
                catch
                {
                    resolvedFileName = "downloaded_file.bin";
                }
            }

            var client = _httpClientFactory.CreateClient();
            using var response = await client.GetAsync(resolvedUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            var contentType = response.Content.Headers.ContentType?.MediaType;

            var uniqueKey = $"{messageGuid}_{step.PublicId}_{Convert.ToHexString(executionPathHash)}";
            var storedFile = await fileStorageService.SaveAsync(contentStream, resolvedFileName, contentType, ct, uniqueKey);

            if (!string.IsNullOrEmpty(config.FileRecordStepId))
            {
                var targetStep = allSteps.FirstOrDefault(s => s.Id.ToString() == config.FileRecordStepId || s.RefId == config.FileRecordStepId);
                if (targetStep != null)
                {
                    string? targetTableId = null;
                    if (!string.IsNullOrEmpty(targetStep.ConfigJson))
                    {
                        using var doc = JsonDocument.Parse(targetStep.ConfigJson);
                        if (doc.RootElement.TryGetProperty("tableId", out var prop))
                        {
                            targetTableId = prop.GetString();
                        }
                        else if (doc.RootElement.TryGetProperty("tableLabel", out var propLabel))
                        {
                            targetTableId = propLabel.GetString();
                        }
                    }

                    if (!string.IsNullOrEmpty(targetTableId))
                    {
                        var tableGuid = Guid.Parse(targetTableId);
                        var table = await tableRepo.GetByPublicIdAsync(tableGuid, ct);
                        var fields = await fieldRepo.ListByTableAsync(table.Id, ct);

                        Guid recordPublicId = Guid.Empty;
                        if (targetStep.Type == "trigger")
                        {
                            if (contextDict.TryGetValue("trigger", out var triggerObj) && triggerObj is Dictionary<string, object> triggerDict)
                            {
                                if (triggerDict.TryGetValue("RecordPublicId", out var rVal) && Guid.TryParse(rVal?.ToString(), out var g))
                                    recordPublicId = g;
                                else if (triggerDict.TryGetValue("RecordId", out var rVal2) && Guid.TryParse(rVal2?.ToString(), out var g2))
                                    recordPublicId = g2;
                            }
                        }
                        else
                        {
                            if (stepsDict.TryGetValue(targetStep.RefId, out var stepOutputObj))
                            {
                                var jsonStr = JsonSerializer.Serialize(stepOutputObj);
                                using var outputDoc = JsonDocument.Parse(jsonStr);
                                var root = outputDoc.RootElement;
                                if (root.TryGetProperty("CreatedRecordPublicId", out var p1) && Guid.TryParse(p1.GetString(), out var g1))
                                    recordPublicId = g1;
                                else if (root.TryGetProperty("UpdatedRecordPublicId", out var p2) && Guid.TryParse(p2.GetString(), out var g2))
                                    recordPublicId = g2;
                                else if (root.TryGetProperty("RecordPublicId", out var p3) && Guid.TryParse(p3.GetString(), out var g3))
                                    recordPublicId = g3;
                                else if (root.TryGetProperty("RecordId", out var p4) && Guid.TryParse(p4.GetString(), out var g4))
                                    recordPublicId = g4;
                                else if (root.TryGetProperty("id", out var p5) && Guid.TryParse(p5.GetString(), out var g5))
                                    recordPublicId = g5;
                            }
                        }

                        if (recordPublicId != Guid.Empty)
                        {
                            var updateValues = new Dictionary<long, object?>();
                            var fileJson = JsonSerializer.Serialize(new
                            {
                                Name = storedFile.Name,
                                Path = storedFile.Path,
                                Size = storedFile.Size,
                                ContentType = storedFile.ContentType
                            });

                            if (config.SelectedFileFields != null && config.SelectedFileFields.Count > 0)
                            {
                                foreach (var mapping in config.SelectedFileFields)
                                {
                                    if (string.IsNullOrEmpty(mapping.Field)) continue;
                                    var field = fields.FirstOrDefault(f =>
                                        f.Name.Equals(mapping.Field, StringComparison.OrdinalIgnoreCase) ||
                                        $"fid_{f.Id}".Equals(mapping.Field, StringComparison.OrdinalIgnoreCase) ||
                                        $"fid_{f.Fid}".Equals(mapping.Field, StringComparison.OrdinalIgnoreCase));

                                    if (field != null && field.Fid.HasValue)
                                    {
                                        updateValues[field.Fid.Value] = fileJson;
                                    }
                                }
                            }
                            else if (!string.IsNullOrEmpty(config.TargetFileField))
                            {
                                var field = fields.FirstOrDefault(f =>
                                    f.Name.Equals(config.TargetFileField, StringComparison.OrdinalIgnoreCase) ||
                                    $"fid_{f.Id}".Equals(config.TargetFileField, StringComparison.OrdinalIgnoreCase) ||
                                    $"fid_{f.Fid}".Equals(config.TargetFileField, StringComparison.OrdinalIgnoreCase));

                                if (field != null && field.Fid.HasValue)
                                {
                                    updateValues[field.Fid.Value] = fileJson;
                                }
                            }

                            if (updateValues.Count > 0)
                            {
                                await uow.BeginAsync(ct);
                                try
                                {
                                    var cachedOutput = await idempotencyRepo.GetByExecutionKeyAsync(messageGuid, step.PublicId, executionPathHash, uow.Transaction, ct);
                                    if (!string.IsNullOrEmpty(cachedOutput))
                                    {
                                        await uow.CommitAsync(ct);
                                        return cachedOutput;
                                    }

                                    await recordWriteService.ApplyAsync(
                                        table, fields, recordPublicId, updateValues, AuditActions.Updated, "File uploaded via Pipeline action step", ct, uow.Transaction);

                                    var outputJson = JsonSerializer.Serialize(new
                                    {
                                        Name = storedFile.Name,
                                        Path = storedFile.Path,
                                        Size = storedFile.Size,
                                        ContentType = storedFile.ContentType
                                    });

                                    await idempotencyRepo.InsertAsync(new PipelineStepIdempotencyLog
                                    {
                                        MessageId = messageGuid,
                                        StepPublicId = step.PublicId,
                                        ExecutionPathHash = executionPathHash,
                                        ExecutionPath = executionPath,
                                        OutputJson = outputJson
                                    }, uow.Transaction, ct);

                                    await uow.CommitAsync(ct);
                                    return outputJson;
                                }
                                catch (Exception ex) when (IsUniqueConstraintViolation(ex))
                                {
                                    await uow.RollbackAsync(CancellationToken.None);
                                    var winningOutput = await idempotencyRepo.GetByExecutionKeyAsync(messageGuid, step.PublicId, executionPathHash, null, ct);
                                    if (winningOutput != null) return winningOutput;
                                    throw;
                                }
                                catch
                                {
                                    await uow.RollbackAsync(CancellationToken.None);
                                    throw;
                                }
                            }
                        }
                    }
                }
            }

            return JsonSerializer.Serialize(new
            {
                Name = storedFile.Name,
                Path = storedFile.Path,
                Size = storedFile.Size,
                ContentType = storedFile.ContentType
            });
        }
        else if (step.Type == "trigger" && (subtype == "new-event" || subtype == "new-bulk-event" || subtype == "record-added" || subtype == "record-updated" || subtype == "record-deleted" || subtype == "schedule" || subtype == "webhook"))
        {
            var triggerInfo = new Dictionary<string, object?>();
            triggerInfo["Status"] = "Fired";
            triggerInfo["TriggerType"] = subtype switch
            {
                "new-bulk-event" => "On New Bulk Event",
                "record-added" => "On Record Added",
                "record-updated" => "On Record Updated",
                "record-deleted" => "On Record Deleted",
                "schedule" => "On Schedule",
                "webhook" => "On Webhook",
                _ => "On New Event"
            };

            if (contextDict.TryGetValue("trigger", out var triggerObj) && triggerObj != null)
            {
                if (triggerObj is Dictionary<string, object> dict)
                {
                    foreach (var kvp in dict)
                    {
                        triggerInfo[kvp.Key] = kvp.Value;
                    }
                }
                else if (triggerObj is JsonElement el && el.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in el.EnumerateObject())
                    {
                        triggerInfo[prop.Name] = ConvertJsonElement(prop.Value);
                    }
                }
            }

            if (!triggerInfo.ContainsKey("MessageId") && contextDict.TryGetValue("_CorrelationId", out var corrId))
            {
                triggerInfo["CorrelationId"] = corrId;
            }

            var outputJson = JsonSerializer.Serialize(triggerInfo);
            stepRun.InputContext = outputJson;
            return outputJson;
        }
        else
        {
            _logger.LogError("Step type '{Type}' / subtype '{Subtype}' is not supported by the execution engine.", step.Type, step.Subtype);
            throw new NotSupportedException($"Step type '{step.Type}' / subtype '{step.Subtype}' is not supported by the execution engine.");
        }
    }

    private Dictionary<string, BulkUpsertSession> GetOrCreateBulkUpsertSessions(Dictionary<string, object> contextDict)
    {
        if (!contextDict.TryGetValue("_bulkUpsertSessions", out var sessionsObj) || sessionsObj is not Dictionary<string, BulkUpsertSession> sessions)
        {
            sessions = new Dictionary<string, BulkUpsertSession>();
            contextDict["_bulkUpsertSessions"] = sessions;
        }
        return sessions;
    }

    private IEnumerable<object>? GetLoopCollection(object? sourceVal)
    {
        if (sourceVal == null) return null;

        if (sourceVal is string str && !string.IsNullOrWhiteSpace(str))
        {
            try
            {
                using var doc = JsonDocument.Parse(str);
                var root = doc.RootElement.Clone();
                if (root.ValueKind == JsonValueKind.Array)
                {
                    return root.EnumerateArray().Select(e => (object)e.Clone()).ToList();
                }
                if (root.ValueKind == JsonValueKind.Object && TryGetLoopArray(root, out var recs))
                {
                    return recs.EnumerateArray().Select(e => (object)e.Clone()).ToList();
                }
            }
            catch { }
        }

        if (sourceVal is JsonElement jsonEl)
        {
            if (jsonEl.ValueKind == JsonValueKind.Array)
            {
                return jsonEl.EnumerateArray().Cast<object>();
            }
            if (jsonEl.ValueKind == JsonValueKind.Object)
            {
                if (TryGetLoopArray(jsonEl, out var recordsProp))
                {
                    return recordsProp.EnumerateArray().Select(e => (object)e.Clone()).ToList();
                }
            }
        }
        
        if (sourceVal is IDictionary<string, object> dict)
        {
            if (dict.TryGetValue("records", out var recs) && recs is IEnumerable<object> recList)
            {
                return recList;
            }
        }
        
        if (sourceVal is IEnumerable<object> enumerable)
        {
            return enumerable;
        }

        return null;
    }

    private static bool TryGetLoopArray(JsonElement value, out JsonElement array)
    {
        foreach (var propertyName in new[] { "records", "data", "items", "results" })
        {
            if (value.TryGetProperty(propertyName, out array) && array.ValueKind == JsonValueKind.Array)
                return true;
        }

        // Custom APIs often wrap their list in one application-specific key.
        // Accept that unambiguous shape without guessing when several arrays exist.
        var arrays = value.EnumerateObject().Where(property => property.Value.ValueKind == JsonValueKind.Array).ToList();
        if (arrays.Count == 1)
        {
            array = arrays[0].Value;
            return true;
        }

        array = default;
        return false;
    }

    private static bool TryParseDateTime(string input, out DateTime date)
    {
        return PipelineFilterEvaluator.TryParseDateTime(input, out date);
    }

    private static object? ConvertJsonElement(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                return el.GetString();
            case JsonValueKind.Number:
                if (el.TryGetInt32(out var i)) return i;
                if (el.TryGetInt64(out var l)) return l;
                if (el.TryGetDecimal(out var d)) return d;
                return el.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null:
                return null;
            default:
                return el;
        }
    }

    private bool EvaluateConditionOperator(string leftVal, string op, string rightVal)
    {
        return PipelineFilterEvaluator.EvaluateConditionOperator(leftVal, op, rightVal, logger: _logger);
    }

    private static bool IsRuleNodeCompletelyBlank(ConditionRuleNode rule)
    {
        if (rule == null) return true;
        if (rule.Type == "nested")
        {
            if (rule.Groups == null || !rule.Groups.Any()) return true;
            return rule.Groups.All(g => IsGroupCompletelyBlank(g));
        }
        return string.IsNullOrWhiteSpace(rule.Left) &&
               (string.IsNullOrWhiteSpace(rule.Op) || rule.Op.Equals("equals", StringComparison.OrdinalIgnoreCase)) &&
               string.IsNullOrWhiteSpace(rule.Right);
    }

    private static bool IsGroupCompletelyBlank(ConditionRuleGroup group)
    {
        if (group == null || group.Rules == null || !group.Rules.Any()) return true;
        return group.Rules.All(r => IsRuleNodeCompletelyBlank(r));
    }

    private async Task<bool> EvaluateConditionRuleGroupsAsync(
        List<ConditionRuleGroup> groups,
        string payloadJson,
        string? executionPath,
        IAppFieldRepository fieldRepo,
        IAppTableRepository tableRepo,
        List<PipelineStep>? allSteps,
        List<object> auditTrail,
        CancellationToken ct)
    {
        if (groups == null || !groups.Any()) return false;
        // Top-level RuleGroups are joined using OR / Any semantics
        bool matchedAny = false;
        foreach (var group in groups)
        {
            var res = await EvaluateConditionGroupAsync(group, payloadJson, executionPath, fieldRepo, tableRepo, allSteps, auditTrail, ct);
            if (res) matchedAny = true;
        }
        return matchedAny;
    }

    private async Task<bool> EvaluateConditionGroupAsync(
        ConditionRuleGroup group,
        string payloadJson,
        string? executionPath,
        IAppFieldRepository fieldRepo,
        IAppTableRepository tableRepo,
        List<PipelineStep>? allSteps,
        List<object> auditTrail,
        CancellationToken ct)
    {
        if (group == null || group.Rules == null || !group.Rules.Any()) return false;

        if (!string.IsNullOrWhiteSpace(group.LogicalOp) &&
            !group.LogicalOp.Equals("AND", StringComparison.OrdinalIgnoreCase) &&
            !group.LogicalOp.Equals("OR", StringComparison.OrdinalIgnoreCase))
        {
            throw new PipelineStepException($"Invalid Condition LogicalOp '{group.LogicalOp}'. Allowed values are AND or OR.");
        }

        if (group.LogicalOp != null && string.IsNullOrWhiteSpace(group.LogicalOp))
        {
            throw new PipelineStepException($"Invalid Condition LogicalOp '{group.LogicalOp}'. Allowed values are AND or OR.");
        }

        var activeRules = group.Rules.Where(r => !IsRuleNodeCompletelyBlank(r)).ToList();
        if (!activeRules.Any()) return false;

        bool isAnd = group.LogicalOp?.Equals("AND", StringComparison.OrdinalIgnoreCase) == true;

        foreach (var rule in activeRules)
        {
            bool ruleResult = false;
            if (rule.Type == "rule" || string.IsNullOrEmpty(rule.Type))
            {
                if (string.IsNullOrWhiteSpace(rule.Left) || string.IsNullOrWhiteSpace(rule.Op))
                {
                    ruleResult = false;
                }
                else
                {
                    var leftVal = EvaluateTokens(rule.Left, payloadJson, executionPath, allSteps);
                    var rightVal = EvaluateTokens(rule.Right, payloadJson, executionPath, allSteps);

                    var leftCategory = await ResolveRuleTypeCategoryAsync(rule.Left, payloadJson, allSteps, fieldRepo, tableRepo, ct);

                    if (!string.IsNullOrWhiteSpace(rule.Right) && System.Text.RegularExpressions.Regex.IsMatch(rule.Right.Trim(), @"\{\{\s*(?:steps\.|trigger\.|variables\.)"))
                    {
                        var rightCategory = await ResolveRuleTypeCategoryAsync(rule.Right, payloadJson, allSteps, fieldRepo, tableRepo, ct);
                        if (!leftCategory.Equals(rightCategory, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new PipelineStepException($"Incompatible dynamic operand types: Left operand '{rule.Left}' ({leftCategory}) vs Right operand '{rule.Right}' ({rightCategory}).");
                        }
                    }

                    ruleResult = PipelineFilterEvaluator.EvaluateConditionOperator(leftVal, rule.Op ?? "equals", rightVal, leftCategory, _logger);

                    auditTrail?.Add(new {
                        Type = "rule",
                        LeftToken = rule.Left,
                        LeftResolved = leftVal,
                        Op = rule.Op,
                        RightToken = rule.Right,
                        RightResolved = rightVal,
                        TypeCategory = leftCategory,
                        Matched = ruleResult
                    });
                }
            }
            else if (rule.Type == "nested" && rule.Groups != null && rule.Groups.Any())
            {
                var activeSubGroups = rule.Groups.Where(sg => !IsGroupCompletelyBlank(sg)).ToList();
                if (activeSubGroups == null || !activeSubGroups.Any())
                {
                    ruleResult = false;
                }
                else
                {
                    // Nested rule.Groups sibling groups are joined using OR (Any) semantics
                    ruleResult = false;
                    foreach (var subGrp in activeSubGroups)
                    {
                        var subRes = await EvaluateConditionGroupAsync(subGrp, payloadJson, executionPath, fieldRepo, tableRepo, allSteps, auditTrail, ct);
                        if (subRes) ruleResult = true;
                    }
                }
            }
            else
            {
                ruleResult = false; // Malformed / unknown rule type fails closed
            }

            if (isAnd && !ruleResult) return false;
            if (!isAnd && ruleResult) return true;
        }

        return isAnd;
    }

    private async Task<string> ResolveRuleTypeCategoryAsync(
        string? token,
        string payloadJson,
        List<PipelineStep>? allSteps,
        IAppFieldRepository fieldRepo,
        IAppTableRepository tableRepo,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return "TEXT";

        var trimmedToken = token.Trim();
        bool isDynamicToken = System.Text.RegularExpressions.Regex.IsMatch(trimmedToken, @"\{\{\s*(?:steps\.|trigger\.|variables\.|[a-zA-Z0-9_]+\.)");
        if (!isDynamicToken)
        {
            return "TEXT"; // Static literal value defaults to category TEXT
        }

        // Match {{steps.ref_1001.fid_5}}, {{steps.ref_loop.item.fid_5}}, {{ref_1001.fid_5}}, or {{trigger.fid_3}}
        var match = System.Text.RegularExpressions.Regex.Match(trimmedToken, @"\{\{\s*(?:steps\.)?([a-zA-Z0-9_]+)(?:\.[a-zA-Z0-9_]+)*\.([a-zA-Z0-9_]+)\s*\}\}");
        if (!match.Success)
        {
            match = System.Text.RegularExpressions.Regex.Match(trimmedToken, @"\{\{\s*(trigger)\.([a-zA-Z0-9_]+)\s*\}\}");
        }

        if (!match.Success)
        {
            throw new PipelineStepException($"Condition token '{token}' is malformed and could not be parsed.");
        }

        var stepRef = match.Groups[1].Value;
        var fieldRef = match.Groups[2].Value;

        PipelineStep? sourceStep = null;
        if (allSteps != null && allSteps.Count > 0)
        {
            if (stepRef.Equals("trigger", StringComparison.OrdinalIgnoreCase))
            {
                sourceStep = allSteps.FirstOrDefault(s => s.Type == "trigger");
            }
            else
            {
                sourceStep = allSteps.FirstOrDefault(s => string.Equals(s.RefId, stepRef, StringComparison.OrdinalIgnoreCase) || string.Equals(s.Id.ToString(), stepRef, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (sourceStep == null || string.IsNullOrWhiteSpace(sourceStep.ConfigJson))
        {
            if (allSteps == null || allSteps.Count == 0 || (sourceStep != null && sourceStep.Type == "trigger"))
            {
                return "TEXT";
            }
            throw new PipelineStepException($"Source step '{stepRef}' referenced in condition token '{token}' could not be resolved.");
        }

        if (sourceStep.Subtype == "pipeline-called")
        {
            var value = ResolveCallableValue(trimmedToken, payloadJson, null, allSteps);
            return value switch
            {
                bool => "BOOLEAN",
                byte or short or int or long or float or double or decimal => "NUMBER",
                _ => "TEXT"
            };
        }

        // If source step is a Loop step, resolve table metadata from its loopOverStepId step
        if (sourceStep.Type == "loop" || sourceStep.Subtype == "for-each")
        {
            string? loopOverStepId = null;
            using (var loopDoc = JsonDocument.Parse(sourceStep.ConfigJson))
            {
                var loopRoot = loopDoc.RootElement;
                if ((loopRoot.TryGetProperty("loopOverStepId", out var lProp) || loopRoot.TryGetProperty("LoopOverStepId", out lProp)) && lProp.ValueKind == JsonValueKind.String)
                {
                    loopOverStepId = lProp.GetString();
                }
            }

            if (string.IsNullOrEmpty(loopOverStepId))
            {
                throw new PipelineStepException($"Loop step '{stepRef}' referenced in condition token '{token}' does not have a valid target step (loopOverStepId).");
            }

            var collectionStep = allSteps?.FirstOrDefault(s => string.Equals(s.RefId, loopOverStepId, StringComparison.OrdinalIgnoreCase) || string.Equals(s.Id.ToString(), loopOverStepId, StringComparison.OrdinalIgnoreCase));
            if (collectionStep == null || string.IsNullOrWhiteSpace(collectionStep.ConfigJson))
            {
                throw new PipelineStepException($"Collection source step '{loopOverStepId}' referenced by loop step '{stepRef}' for token '{token}' could not be resolved.");
            }

            sourceStep = collectionStep;
        }

        Guid tablePublicId = Guid.Empty;
        string? connectionPublicId = null;
        using (var doc = JsonDocument.Parse(sourceStep.ConfigJson))
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("tableId", out var tProp) ||
                root.TryGetProperty("tablePublicId", out tProp) ||
                root.TryGetProperty("TableId", out tProp) ||
                root.TryGetProperty("TablePublicId", out tProp))
            {
                if (tProp.ValueKind == JsonValueKind.String && Guid.TryParse(tProp.GetString(), out var g))
                {
                    tablePublicId = g;
                }
            }

            if (root.TryGetProperty("connectionPublicId", out var cProp) ||
                root.TryGetProperty("ConnectionPublicId", out cProp) ||
                root.TryGetProperty("connection", out cProp) ||
                root.TryGetProperty("Connection", out cProp))
            {
                if (cProp.ValueKind == JsonValueKind.String)
                {
                    connectionPublicId = cProp.GetString();
                }
            }
        }

        if (tablePublicId == Guid.Empty)
        {
            throw new PipelineStepException($"Table ID for source step '{sourceStep.RefId}' referenced in condition token '{token}' could not be found.");
        }

        IAppTableRepository effectiveTableRepo = tableRepo;
        IAppFieldRepository effectiveFieldRepo = fieldRepo;
        IDisposable? scopeToDispose = null;

        if (Guid.TryParse(connectionPublicId, out var connectionGuid) && !PipelineStepValidator.SystemConnectionIds.Contains(connectionGuid))
        {
            var resolvedTenantId = await _adminRepo.GetTenantIdByPublicIdAsync(connectionGuid, ct);
            if (resolvedTenantId.HasValue)
            {
                if (resolvedTenantId.Value != _queryContext.TenantId)
                {
                    var scope = _serviceScopeFactory.CreateScope();
                    scopeToDispose = scope;
                    var scopedQueryContext = scope.ServiceProvider.GetRequiredService<IQueryContext>();
                    scopedQueryContext.SetTenantId(resolvedTenantId.Value);
                    scopedQueryContext.IsPipelineExecution = _queryContext.IsPipelineExecution;
                    scopedQueryContext.PipelineDepth = _queryContext.PipelineDepth;
                    scopedQueryContext.PipelineChainJson = _queryContext.PipelineChainJson;
                    scopedQueryContext.SetUserIdentity(
                        _queryContext.UserId,
                        _queryContext.IsSuperAdmin,
                        _queryContext.UserName,
                        _queryContext.UserEmail,
                        _queryContext.Permissions,
                        _queryContext.TenantRole);

                    var scopedTenantRepo = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
                    long actingUserId = _queryContext.UserId;
                    try
                    {
                        using var pDoc = JsonDocument.Parse(payloadJson);
                        if (pDoc.RootElement.TryGetProperty("_CreatedBy", out var cbProp) && cbProp.TryGetInt64(out var cbVal))
                        {
                            actingUserId = cbVal;
                        }
                    }
                    catch { }

                    var isMember = await scopedTenantRepo.IsActiveMemberAsync(actingUserId, ct);
                    if (!isMember)
                    {
                        throw new UnauthorizedAccessException($"Execution authority user {actingUserId} is not an active member of target tenant {resolvedTenantId.Value}.");
                    }

                    effectiveTableRepo = scope.ServiceProvider.GetRequiredService<IAppTableRepository>();
                    effectiveFieldRepo = scope.ServiceProvider.GetRequiredService<IAppFieldRepository>();
                }
            }
            else
            {
                var connectionScopeResolver = _serviceProvider.GetService<Connections.Common.ConnectionScopeResolver>();
                if (connectionScopeResolver != null)
                {
                    long actingUserId = _queryContext.UserId;
                    try
                    {
                        using var pDoc = JsonDocument.Parse(payloadJson);
                        if (pDoc.RootElement.TryGetProperty("_CreatedBy", out var cbProp) && cbProp.TryGetInt64(out var cbVal))
                        {
                            actingUserId = cbVal;
                        }
                    }
                    catch { }

                    var accountScope = await connectionScopeResolver.TryResolveForUserAsync(connectionGuid, actingUserId, ct);
                    if (accountScope != null)
                    {
                        var accountScopeHandle = await Connections.Common.TargetTenantScopeHelper.OpenAsync(_serviceScopeFactory, accountScope, ct);
                        scopeToDispose = accountScopeHandle;
                        effectiveTableRepo = accountScopeHandle.GetRequiredService<IAppTableRepository>();
                        effectiveFieldRepo = accountScopeHandle.GetRequiredService<IAppFieldRepository>();
                    }
                    else
                    {
                        throw new PipelineStepException($"Connection '{connectionGuid}' referenced by source step '{sourceStep.RefId}' could not be resolved or access was denied.");
                    }
                }
            }
        }

        try
        {
            var table = await effectiveTableRepo.GetByPublicIdAsync(tablePublicId, ct);
            if (table == null)
            {
                throw new PipelineStepException($"Table '{tablePublicId}' referenced in condition step could not be found in metadata.");
            }

            var fields = await effectiveFieldRepo.ListByTableAsync(table.Id, ct);
            if (fields == null || !fields.Any())
            {
                throw new PipelineStepException($"Fields for table '{table.Name}' could not be retrieved.");
            }

            AppField? matchedField = ResolvePipelineField(fieldRef, fields);

            if (matchedField == null)
            {
                throw new PipelineStepException($"Field metadata for token '{token}' (field '{fieldRef}') could not be resolved.");
            }

            return PipelineFilterEvaluator.GetTypeCategory(matchedField.TypeCode);
        }
        finally
        {
            scopeToDispose?.Dispose();
        }
    }

    public static AppField? ResolvePipelineField(string? fieldRef, IEnumerable<AppField>? fields)
    {
        if (string.IsNullOrWhiteSpace(fieldRef) || fields == null) return null;

        // 1. Primary canonical format: fid_<FID> (e.g. fid_3, fid_15, fid_6)
        if (fieldRef.StartsWith("fid_", StringComparison.OrdinalIgnoreCase) && int.TryParse(fieldRef.Substring(4), out var fidNum))
        {
            return fields.FirstOrDefault(f => f.Fid == fidNum);
        }

        // 2. Direct numeric FID
        if (int.TryParse(fieldRef, out var directFid))
        {
            return fields.FirstOrDefault(f => f.Fid == directFid);
        }

        // 3. Fallback: match by Name, Label, PhysicalColumnName, or fid_<Id>
        return fields.FirstOrDefault(f =>
            string.Equals(f.Name, fieldRef, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(f.Label, fieldRef, StringComparison.OrdinalIgnoreCase) ||
            (f.IsSystem && string.Equals(f.PhysicalColumnName, fieldRef, StringComparison.OrdinalIgnoreCase)) ||
            $"fid_{f.Id}".Equals(fieldRef, StringComparison.OrdinalIgnoreCase));
    }

    private string EvaluateTokens(string? input, string payloadJson, string? executionPath = null, List<PipelineStep>? allSteps = null)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        if (string.IsNullOrEmpty(payloadJson)) return input;
        input = Regex.Replace(input, @"(?<=\{\{)\s*(?:steps\.)?([A-Za-z][A-Za-z0-9_]*)\._metadata\.", " request_metadata.$1.");

        var callableTrigger = allSteps?.FirstOrDefault(step => step.Subtype == "pipeline-called" && step.Type == "trigger");
        if (callableTrigger != null)
        {
            var definition = CallablePipelineDefinition.ValidateConfig(callableTrigger.ConfigJson, false);
            foreach (Match reference in Regex.Matches(input, @"\{\{\s*(?:steps\.)?([A-Za-z][A-Za-z0-9_]*)\.([A-Za-z][A-Za-z0-9_]*)"))
            {
                if ((reference.Groups[1].Value == callableTrigger.RefId || reference.Groups[1].Value == "trigger") &&
                    !definition.Arguments.Contains(reference.Groups[2].Value, StringComparer.Ordinal))
                    throw new PipelineStepException($"Callable argument '{reference.Groups[2].Value}' no longer exists.");
            }
        }

        // Structured fallback for legacy compatibility
        if (!string.IsNullOrEmpty(executionPath) && allSteps != null)
        {
            var pathParts = executionPath.Split('/');
            foreach (var part in pathParts)
            {
                var loopStep = allSteps.FirstOrDefault(s => (s.RefId == part || string.Equals(s.Id.ToString(), part, StringComparison.OrdinalIgnoreCase)) && (s.Type == "loop" || s.Subtype == "for-each"));
                if (loopStep != null && !string.IsNullOrEmpty(loopStep.ConfigJson))
                {
                    try
                    {
                        using var loopDoc = JsonDocument.Parse(loopStep.ConfigJson);
                        var loopRoot = loopDoc.RootElement;
                        if ((loopRoot.TryGetProperty("loopOverStepId", out var loopOverProp) || loopRoot.TryGetProperty("LoopOverStepId", out loopOverProp)) && loopOverProp.ValueKind == JsonValueKind.String)
                        {
                            var loopOverStepId = loopOverProp.GetString();
                            if (!string.IsNullOrEmpty(loopOverStepId))
                            {
                                var targetStep = allSteps.FirstOrDefault(s => string.Equals(s.Id.ToString(), loopOverStepId, StringComparison.OrdinalIgnoreCase) || string.Equals(s.RefId, loopOverStepId, StringComparison.OrdinalIgnoreCase));
                                if (targetStep != null)
                                {
                                    var targetPattern = $@"(?:\bsteps\.)?(?:{Regex.Escape(targetStep.RefId)}|{Regex.Escape(targetStep.Id.ToString())})(?=\.(?!records\b)[a-zA-Z0-9_]+)";
                                    input = Regex.Replace(input, targetPattern, $"steps.{loopStep.RefId}.item", RegexOptions.IgnoreCase);
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Ignore config parsing errors
                    }
                }
            }
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;

            var contextDict = new Dictionary<string, object>();
            var stepsDict = new StepsDictionary();
            contextDict["steps"] = stepsDict;

            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name == "steps")
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var sProp in prop.Value.EnumerateObject())
                            {
                                stepsDict[sProp.Name] = sProp.Value;
                            }
                        }
                    }
                    else if (prop.Name == "trigger")
                    {
                        contextDict["trigger"] = prop.Value;
                        stepsDict["trigger"] = prop.Value;
                    }
                    else
                    {
                        contextDict[prop.Name] = ConvertJsonElement(prop.Value)!;
                    }
                }
            }

            if (!stepsDict.ContainsKey("trigger"))
            {
                var triggerData = new Dictionary<string, object>();
                if (root.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in root.EnumerateObject())
                    {
                        if (prop.Name.StartsWith("fid_") || prop.Name.Equals("id", StringComparison.OrdinalIgnoreCase) || prop.Name.Equals("publicId", StringComparison.OrdinalIgnoreCase))
                        {
                            triggerData[prop.Name] = ConvertJsonElement(prop.Value)!;
                        }
                    }
                }
                contextDict["trigger"] = triggerData;
                stepsDict["trigger"] = triggerData;
            }

            // Normalize legacy un-prefixed step tokens (e.g. {{ref_9356.fid_6}} -> {{steps.ref_9356.fid_6}})
            input = System.Text.RegularExpressions.Regex.Replace(input, @"\{\{\s*([a-zA-Z0-9_]+)(\.[^|}]+?)(\s*\|.*)?\s*\}\}", match =>
            {
                var firstSegment = match.Groups[1].Value;
                var rest = match.Groups[2].Value;
                var filterPipe = match.Groups[3].Value;

                if (firstSegment.Equals("steps", StringComparison.OrdinalIgnoreCase) ||
                    firstSegment.Equals("trigger", StringComparison.OrdinalIgnoreCase) ||
                    firstSegment.Equals("ERROR", StringComparison.Ordinal) ||
                    firstSegment.Equals("variables", StringComparison.OrdinalIgnoreCase))
                {
                    return match.Value;
                }

                bool isValidStepRef = stepsDict.Keys.Any(k => string.Equals(k, firstSegment, StringComparison.OrdinalIgnoreCase));
                if (!isValidStepRef && allSteps != null)
                {
                    isValidStepRef = allSteps.Any(s => string.Equals(s.RefId, firstSegment, StringComparison.OrdinalIgnoreCase) ||
                                                       string.Equals(s.Id.ToString(), firstSegment, StringComparison.OrdinalIgnoreCase));
                }

                if (isValidStepRef)
                {
                    return "{{" + $"steps.{firstSegment}{rest}{filterPipe}" + "}}";
                }

                return match.Value;
            });

            // Older editor versions prefixed Make Request response properties with fid_.
            // Only translate that legacy spelling when the actual response has the
            // unprefixed property; record FIDs and real fid_* response keys stay intact.
            input = Regex.Replace(input, @"\{\{\s*steps\.([A-Za-z0-9_]+)\.fid_([A-Za-z_][A-Za-z0-9_]*)(?=[.\s|}])", match =>
            {
                var stepRef = match.Groups[1].Value;
                var property = match.Groups[2].Value;
                if (allSteps?.Any(step => step.RefId == stepRef && step.Subtype == "make-request") != true ||
                    !stepsDict.TryGetValue(stepRef, out var output) || output is not JsonElement response ||
                    response.ValueKind != JsonValueKind.Object || response.TryGetProperty("fid_" + property, out _) ||
                    !response.TryGetProperty(property, out _))
                    return match.Value;
                return match.Value.Replace(".fid_" + property, "." + property, StringComparison.Ordinal);
            });

            var context = new CustomTemplateContext(contextDict);
            context.MemberRenamer = member => member.Name;

            // Push default Scriban builtins
            context.PushGlobal(Scriban.TemplateContext.GetDefaultBuiltinObject());

            var scriptObject = new Scriban.Runtime.ScriptObject();
            foreach (var kvp in contextDict)
            {
                scriptObject.Add(kvp.Key, kvp.Value);
            }

            // Register Jinja-compatible filters as native custom delegates
            scriptObject.Import("to_string", new Func<object?, string>(val => val?.ToString() ?? string.Empty));
            scriptObject.Import("to_int", new Func<object?, object?>(val => {
                if (val == null) return null;
                if (val is bool b) return b ? 1 : 0;
                var str = val.ToString()?.Trim();
                if (string.IsNullOrEmpty(str)) return null;
                if (decimal.TryParse(str, out var d)) return (int)Math.Truncate(d);
                return null;
            }));
            scriptObject.Import("to_float", new Func<object?, object?>(val => {
                if (val == null) return null;
                var str = val.ToString()?.Trim();
                if (string.IsNullOrEmpty(str)) return null;
                if (double.TryParse(str, out var d)) return d;
                return null;
            }));
            scriptObject.Import("to_json", new Func<object, string>(val => JsonSerializer.Serialize(val)));
            scriptObject.Import("from_json", new Func<string, object?>(val => string.IsNullOrWhiteSpace(val) ? null : JsonSerializer.Deserialize<object>(val)));
            scriptObject.Import("join", new Func<System.Collections.IEnumerable, string, string>((list, sep) => {
                if (list == null) return string.Empty;
                var stringList = new List<string>();
                foreach (var item in list) stringList.Add(item?.ToString() ?? string.Empty);
                return string.Join(sep, stringList);
            }));
            scriptObject.Import("length", new Func<object, int>(val => {
                if (val == null) return 0;
                if (val is System.Collections.ICollection col) return col.Count;
                if (val is string str) return str.Length;
                if (val is JsonElement el)
                {
                    if (el.ValueKind == JsonValueKind.Array) return el.GetArrayLength();
                    if (el.ValueKind == JsonValueKind.Object) return el.EnumerateObject().Count();
                }
                return 0;
            }));
            scriptObject.Import("now", new Func<DateTime>(() => DateTime.UtcNow));
            scriptObject.Import("format_datetime", new Func<object, string, string>((val, format) => {
                if (val == null) return string.Empty;
                if (val is DateTime dt) return dt.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
                if (val is string str && DateTime.TryParse(str, out var dtParsed)) return dtParsed.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
                return val.ToString() ?? string.Empty;
            }));

            context.PushGlobal(scriptObject);

            try
            {
                var preprocessedInput = System.Text.RegularExpressions.Regex.Replace(input, @"\{\{((?:(?!\}\}).)*)\}\}", match =>
                {
                    var inner = match.Value;
                    return System.Text.RegularExpressions.Regex.Replace(inner, @"\|\s*(string|int|float)\b(?!\s*\.)", "| to_$1");
                });
                var fullTemplate = Scriban.Template.Parse(preprocessedInput);
                if (!fullTemplate.HasErrors)
                {
                    var rendered = fullTemplate.Render(context);
                    if (!rendered.Contains("[NOT_FOUND]"))
                    {
                        return rendered;
                    }
                }
            }
            catch
            {
                // Fallback to legacy regex replacement on error
            }

            var regex = new System.Text.RegularExpressions.Regex(@"\{\{\s*([a-zA-Z0-9_\[\]\.#]+)\s*\}\}");

            var result = regex.Replace(input, match =>
            {
                var path = match.Groups[1].Value;
                try
                {
                    var template = Scriban.Template.Parse("{?" + path + "?}");
                    var template2 = Scriban.Template.Parse("{{" + path + "}}");
                    if (template2.HasErrors) return match.Value;

                    var evaluatedStr = template2.Render(context);
                    if (evaluatedStr == "[NOT_FOUND]")
                    {
                        return match.Value;
                    }
                    return evaluatedStr;
                }
                catch
                {
                    return match.Value;
                }
            });

            bool isDynamicInput = System.Text.RegularExpressions.Regex.IsMatch(input.Trim(), @"\{\{\s*(?:steps\.|trigger\.|variables\.|[a-zA-Z0-9_]+\.)");
            if (isDynamicInput && (string.IsNullOrEmpty(result) || result.Contains("{{") || result.Contains("[NOT_FOUND]")))
            {
                throw new PipelineStepException($"Failed to resolve dynamic token '{input}' in pipeline step execution context.");
            }

            return result;
        }
        catch (PipelineStepException)
        {
            throw;
        }
        catch (Exception ex)
        {
            bool isDynamicInput = System.Text.RegularExpressions.Regex.IsMatch(input.Trim(), @"\{\{\s*(?:steps\.|trigger\.|variables\.|[a-zA-Z0-9_]+\.)");
            if (isDynamicInput)
            {
                throw new PipelineStepException($"Failed to resolve dynamic token '{input}' in pipeline step execution context: {ex.Message}");
            }
            return input;
        }
    }

    private object? ResolveCallableValue(string? input, string payloadJson, string? executionPath, List<PipelineStep>? allSteps)
    {
        // A complete field reference retains its JSON type; interpolated text remains text.
        var match = Regex.Match(input ?? "", @"^\{\{\s*((?:steps\.|trigger\.|variables\.)?[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z][A-Za-z0-9_]*)+)\s*\}\}$");
        if (!match.Success) return EvaluateTokens(input, payloadJson, executionPath, allSteps);
        var path = match.Groups[1].Value;
        if (allSteps?.Any(step => step.RefId == path.Split('.')[0]) == true) path = "steps." + path;
        using var document = JsonDocument.Parse(payloadJson);
        var value = document.RootElement;
        foreach (var segment in path.Split('.'))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
                throw new PipelineStepException($"Call argument reference '{input}' is missing from the execution context.");
        }
        return ConvertJsonElement(value.Clone());
    }

    private string? ResolvePath(JsonElement root, string path)
    {
        var parts = path.Split('.');
        var current = root;

        foreach (var part in parts)
        {
            var cleanPart = part;
            int? arrayIndex = null;

            var bracketIdx = part.IndexOf('[');
            if (bracketIdx >= 0)
            {
                cleanPart = part.Substring(0, bracketIdx);
                var endBracketIdx = part.IndexOf(']', bracketIdx);
                if (endBracketIdx > bracketIdx && int.TryParse(part.Substring(bracketIdx + 1, endBracketIdx - bracketIdx - 1), out var parsedIdx))
                {
                    arrayIndex = parsedIdx;
                }
            }

            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(cleanPart, out var nextProp))
            {
                current = nextProp;
            }
            else
            {
                if (parts.Length > 1)
                {
                    var lastSegment = parts[^1];
                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(lastSegment, out var rootProp))
                    {
                        current = rootProp;
                        break;
                    }
                }
                return null;
            }

            if (arrayIndex.HasValue)
            {
                if (current.ValueKind == JsonValueKind.Array && arrayIndex.Value < current.GetArrayLength())
                {
                    current = current[arrayIndex.Value];
                }
                else
                {
                    return null;
                }
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString() ?? string.Empty,
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "",
            _ => current.GetRawText()
        };
    }

    private class StepsDictionary : Dictionary<string, object>
    {
    }

    private class CustomTemplateContext : Scriban.TemplateContext
    {
        private readonly Scriban.Runtime.IObjectAccessor _jsonAccessor = new JsonElementAccessor();
        private readonly Scriban.Runtime.IObjectAccessor _stepsAccessor;
        private readonly Scriban.Runtime.IObjectAccessor _proxyAccessor;

        public CustomTemplateContext(Dictionary<string, object> rootContext)
        {
            _stepsAccessor = new StepsDictionaryAccessor(rootContext);
            _proxyAccessor = new FallbackRootProxy(rootContext);
        }

        protected override Scriban.Runtime.IObjectAccessor GetMemberAccessorImpl(object target)
        {
            if (target is JsonElement)
            {
                return _jsonAccessor;
            }
            if (target is StepsDictionary)
            {
                return _stepsAccessor;
            }
            if (target is FallbackRootProxy)
            {
                return _proxyAccessor;
            }
            return base.GetMemberAccessorImpl(target);
        }
    }

    private class StepsDictionaryAccessor : Scriban.Runtime.IObjectAccessor
    {
        private readonly Dictionary<string, object> _rootContext;

        public StepsDictionaryAccessor(Dictionary<string, object> rootContext)
        {
            _rootContext = rootContext;
        }

        public int GetMemberCount(Scriban.TemplateContext context, Scriban.Parsing.SourceSpan span, object target)
        {
            return ((StepsDictionary)target).Count;
        }

        public IEnumerable<string> GetMembers(Scriban.TemplateContext context, Scriban.Parsing.SourceSpan span, object target)
        {
            return ((StepsDictionary)target).Keys;
        }

        public bool HasMember(Scriban.TemplateContext context, Scriban.Parsing.SourceSpan span, object target, string member)
        {
            return true;
        }

        public bool TryGetValue(Scriban.TemplateContext context, Scriban.Parsing.SourceSpan span, object target, string member, out object value)
        {
            var dict = (StepsDictionary)target;
            if (dict.TryGetValue(member, out var val))
            {
                value = val;
                return true;
            }
            value = new FallbackRootProxy(_rootContext);
            return true;
        }

        public bool TrySetValue(Scriban.TemplateContext context, Scriban.Parsing.SourceSpan span, object target, string member, object value)
        {
            return false;
        }

        public bool HasIndexer => true;
        public Type IndexType => typeof(object);

        public bool TryGetItem(Scriban.TemplateContext context, Scriban.Parsing.SourceSpan span, object target, object indexer, out object value)
        {
            value = null!;
            if (indexer is string member)
            {
                return TryGetValue(context, span, target, member, out value);
            }
            return false;
        }

        public bool TrySetItem(Scriban.TemplateContext context, Scriban.Parsing.SourceSpan span, object target, object indexer, object value)
        {
            return false;
        }
    }

    private class FallbackRootProxy : Scriban.Runtime.IObjectAccessor
    {
        private readonly Dictionary<string, object> _rootContext;

        public FallbackRootProxy(Dictionary<string, object> rootContext)
        {
            _rootContext = rootContext;
        }

        public int GetMemberCount(Scriban.TemplateContext context, Scriban.Parsing.SourceSpan span, object target) => 0;
        public IEnumerable<string> GetMembers(Scriban.TemplateContext context, Scriban.Parsing.SourceSpan span, object target) => Enumerable.Empty<string>();
        public bool HasMember(Scriban.TemplateContext context, Scriban.Parsing.SourceSpan span, object target, string member) => true;

        public bool TryGetValue(Scriban.TemplateContext context, Scriban.Parsing.SourceSpan span, object target, string member, out object value)
        {
            if (_rootContext.TryGetValue(member, out var val))
            {
                value = val;
                return true;
            }

            if (_rootContext.TryGetValue("trigger", out var triggerObj))
            {
                if (triggerObj is JsonElement el && el.ValueKind == JsonValueKind.Object && el.TryGetProperty(member, out var prop))
                {
                    value = ConvertJsonElement(prop)!;
                    return true;
                }
                else if (triggerObj is Dictionary<string, object> dict && dict.TryGetValue(member, out var dictVal))
                {
                    value = dictVal;
                    return true;
                }
            }

            value = this;
            return true;
        }

        public bool TrySetValue(Scriban.TemplateContext context, Scriban.Parsing.SourceSpan span, object target, string member, object value) => false;
        public bool HasIndexer => true;
        public Type IndexType => typeof(object);

        public bool TryGetItem(Scriban.TemplateContext context, Scriban.Parsing.SourceSpan span, object target, object indexer, out object value)
        {
            value = null!;
            if (indexer is string member)
            {
                return TryGetValue(context, span, target, member, out value);
            }
            return false;
        }

        public bool TrySetItem(Scriban.TemplateContext context, Scriban.Parsing.SourceSpan span, object target, object indexer, object value) => false;

        private object? ConvertJsonElement(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.String:
                    return el.GetString();
                case JsonValueKind.Number:
                    if (el.TryGetInt32(out var i)) return i;
                    if (el.TryGetInt64(out var l)) return l;
                    if (el.TryGetDecimal(out var d)) return d;
                    return el.GetDouble();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Null:
                    return null;
                default:
                    return el;
            }
        }

        public override string ToString()
        {
            return "[NOT_FOUND]";
        }
    }

    private class JsonElementAccessor : Scriban.Runtime.IObjectAccessor
    {
        public int GetMemberCount(Scriban.TemplateContext context, Scriban.Parsing.SourceSpan span, object target)
        {
            if (target is JsonElement el && el.ValueKind == JsonValueKind.Object)
            {
                int count = 0;
                foreach (var prop in el.EnumerateObject()) count++;
                return count;
            }
            return 0;
        }

        public IEnumerable<string> GetMembers(Scriban.TemplateContext context, Scriban.Parsing.SourceSpan span, object target)
        {
            if (target is JsonElement el && el.ValueKind == JsonValueKind.Object)
            {
                return el.EnumerateObject().Select(p => p.Name);
            }
            return Enumerable.Empty<string>();
        }

        public bool HasMember(Scriban.TemplateContext context, Scriban.Parsing.SourceSpan span, object target, string member)
        {
            if (target is JsonElement el && el.ValueKind == JsonValueKind.Object)
            {
                if (el.TryGetProperty(member, out _)) return true;
                if (member.StartsWith("fid_") && el.TryGetProperty(member.Replace("fid_", "f_"), out _)) return true;
                if (member.StartsWith("f_") && el.TryGetProperty(member.Replace("f_", "fid_"), out _)) return true;
            }
            return false;
        }

        public bool TryGetValue(Scriban.TemplateContext context, Scriban.Parsing.SourceSpan span, object target, string member, out object value)
        {
            value = null!;
            if (target is JsonElement el && el.ValueKind == JsonValueKind.Object)
            {
                if (el.TryGetProperty(member, out var prop))
                {
                    value = ConvertJsonElement(prop)!;
                    return true;
                }
                if (member.StartsWith("fid_"))
                {
                    var physKey = member.Replace("fid_", "f_");
                    if (el.TryGetProperty(physKey, out var physProp))
                    {
                        value = ConvertJsonElement(physProp)!;
                        return true;
                    }
                }
                if (member.StartsWith("f_"))
                {
                    var stableKey = member.Replace("f_", "fid_");
                    if (el.TryGetProperty(stableKey, out var stableProp))
                    {
                        value = ConvertJsonElement(stableProp)!;
                        return true;
                    }
                }
                foreach (var p in el.EnumerateObject())
                {
                    if (string.Equals(p.Name, member, StringComparison.OrdinalIgnoreCase))
                    {
                        value = ConvertJsonElement(p.Value)!;
                        return true;
                    }
                }
            }
            return false;
        }

        public bool TrySetValue(Scriban.TemplateContext context, Scriban.Parsing.SourceSpan span, object target, string member, object value)
        {
            return false;
        }

        public bool HasIndexer => true;

        public Type IndexType => typeof(object);

        public bool TryGetItem(Scriban.TemplateContext context, Scriban.Parsing.SourceSpan span, object target, object indexer, out object value)
        {
            value = null!;
            if (target is JsonElement el)
            {
                if (el.ValueKind == JsonValueKind.Array && indexer is int idx)
                {
                    if (idx >= 0 && idx < el.GetArrayLength())
                    {
                        value = ConvertJsonElement(el[idx])!;
                        return true;
                    }
                }
                else if (el.ValueKind == JsonValueKind.Object && indexer is string member)
                {
                    if (el.TryGetProperty(member, out var prop))
                    {
                        value = ConvertJsonElement(prop)!;
                        return true;
                    }
                    if (member.StartsWith("fid_"))
                    {
                        var physKey = member.Replace("fid_", "f_");
                        if (el.TryGetProperty(physKey, out var physProp))
                        {
                            value = ConvertJsonElement(physProp)!;
                            return true;
                        }
                    }
                    if (member.StartsWith("f_"))
                    {
                        var stableKey = member.Replace("f_", "fid_");
                        if (el.TryGetProperty(stableKey, out var stableProp))
                        {
                            value = ConvertJsonElement(stableProp)!;
                            return true;
                        }
                    }
                    foreach (var p in el.EnumerateObject())
                    {
                        if (string.Equals(p.Name, member, StringComparison.OrdinalIgnoreCase))
                        {
                            value = ConvertJsonElement(p.Value)!;
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        public bool TrySetItem(Scriban.TemplateContext context, Scriban.Parsing.SourceSpan span, object target, object indexer, object value)
        {
            return false;
        }

        private object? ConvertJsonElement(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.String:
                    return el.GetString();
                case JsonValueKind.Number:
                    if (el.TryGetInt32(out var i)) return i;
                    if (el.TryGetInt64(out var l)) return l;
                    if (el.TryGetDecimal(out var d)) return d;
                    return el.GetDouble();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Null:
                    return null;
                default:
                    return el;
            }
        }
    }

    private object? ParseRecordMappingValue(string value, AppField field, string? expression)
    {
        var label = !string.IsNullOrWhiteSpace(field.Label) ? field.Label : field.Name;
        try { return ParseValueType(value, field.TypeCode, label); }
        catch (FormatException ex)
        {
            // Refer to a dynamic source without recording its possibly sensitive value.
            var token = Regex.Match(expression ?? "", @"^\{\{\s*([A-Za-z0-9_.]+)\s*\}\}$");
            var source = token.Success ? $" from '{token.Groups[1].Value}'" : "";
            var phoneFormatted = Regex.IsMatch(value.Trim(), @"^\+?\d[\d\s().]*-\d[\d\s().-]*$") &&
                value.Count(char.IsDigit) >= 7;
            var kind = value.TrimStart().StartsWith('{') ? "an object" :
                value.TrimStart().StartsWith('[') ? "an array" :
                phoneFormatted ? "phone-formatted text" : "text that cannot be converted";
            var suggestion = phoneFormatted ? " Map phone values to a Phone or Text field." : "";
            throw new PipelineMappingException($"Field '{label}' requires a valid {field.TypeCode} value, but its mapping{source} returned {kind}.{suggestion}", ex);
        }
    }

    private object? ParseValueType(string valueStr, string typeCode, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(valueStr)) return null;

        var normalizedCode = typeCode.ToUpperInvariant();

        if (normalizedCode == "CHECKBOX" || normalizedCode == "BOOLEAN")
        {
            if (bool.TryParse(valueStr, out var bVal)) return bVal;
            if (valueStr == "1") return true;
            if (valueStr == "0") return false;
            throw new FormatException($"Validation error: cannot convert value to {typeCode} for field '{fieldName}'.");
        }
        if (new[] { "NUMERIC", "CURRENCY", "PERCENT", "INTEGER", "FLOAT", "NUMBER", "RATING", "DURATION" }.Contains(normalizedCode))
        {
            if (decimal.TryParse(valueStr, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var dVal) ||
                decimal.TryParse(valueStr, out dVal)) return dVal;
            throw new FormatException($"Validation error: cannot convert value to {typeCode} for field '{fieldName}'.");
        }
        if (new[] { "DATE", "DATE_TIME", "DATETIME", "TIME", "TIME_OF_DAY", "TIMESTAMP" }.Contains(normalizedCode))
        {
            if (DateTime.TryParse(valueStr, out var dtVal)) return dtVal;
            throw new FormatException($"Validation error: cannot convert value to {typeCode} for field '{fieldName}'.");
        }

        return valueStr; // Default to string
    }

    private object? ParseBulkUpsertValueType(string? valueStr, string? typeCode)
    {
        if (valueStr == null) return null;

        var normalizedCode = (typeCode ?? "TEXT").ToUpperInvariant();

        // Text types preserve empty string ""
        if (normalizedCode == "TEXT" ||
            normalizedCode == "TEXTMULTILINE" ||
            normalizedCode == "TEXT_MULTI_LINE" ||
            normalizedCode == "RICHTEXT" ||
            normalizedCode == "RICH_TEXT" ||
            normalizedCode == "EMAIL" ||
            normalizedCode == "URL" ||
            normalizedCode == "PHONE" ||
            normalizedCode == "PHONENUMBER" ||
            normalizedCode == "STRING")
        {
            return valueStr;
        }

        // For non-text types, empty / whitespace strings become null
        if (string.IsNullOrWhiteSpace(valueStr))
        {
            return null;
        }

        if (normalizedCode == "CHECKBOX" || normalizedCode == "BOOLEAN" || normalizedCode == "BOOL")
        {
            if (bool.TryParse(valueStr, out var bVal)) return bVal;
            if (valueStr == "1") return true;
            if (valueStr == "0") return false;
            return null;
        }

        if (new[] { "NUMERIC", "CURRENCY", "PERCENT", "INTEGER", "FLOAT", "NUMBER", "DECIMAL", "INT", "RATING", "DURATION" }.Contains(normalizedCode))
        {
            if (decimal.TryParse(valueStr, out var dVal)) return dVal;
            return null;
        }

        if (new[] { "DATE", "DATETIME", "DATE_TIME", "TIME", "TIME_OF_DAY", "TIMESTAMP" }.Contains(normalizedCode))
        {
            if (DateTime.TryParse(valueStr, out var dtVal)) return dtVal;
            return null;
        }

        return valueStr;
    }

    private class CreateRecordStepConfig
    {
        public string? TableId { get; set; }
        public List<FieldMapping>? FieldMappings { get; set; }
    }

    private class UpdateRecordStepConfig
    {
        public string? TableId { get; set; }
        public string? TargetRecordId { get; set; }
        public List<FieldMapping>? FieldMappings { get; set; }
    }

    private class DeleteRecordStepConfig
    {
        public string? TableId { get; set; }
        public string? TargetRecordId { get; set; }
    }

    private class StopStepConfig
    {
        [System.Text.Json.Serialization.JsonConverter(typeof(StringOrPrimitiveJsonConverter))]
        public string? Reason { get; set; }
    }

    private class ConditionStepConfig
    {
        [System.Text.Json.Serialization.JsonConverter(typeof(StringOrPrimitiveJsonConverter))]
        public string? LeftOperand { get; set; }
        public string? Operator { get; set; }
        [System.Text.Json.Serialization.JsonConverter(typeof(StringOrPrimitiveJsonConverter))]
        public string? RightOperand { get; set; }
        public List<ConditionRuleGroup>? RuleGroups { get; set; }
    }

    private class ConditionRuleGroup
    {
        public string LogicalOp { get; set; } = "OR";
        public List<ConditionRuleNode>? Rules { get; set; }
    }

    private class ConditionRuleNode
    {
        public string Type { get; set; } = "rule"; // "rule" or "nested"
        [System.Text.Json.Serialization.JsonConverter(typeof(StringOrPrimitiveJsonConverter))]
        public string? Left { get; set; }
        public string? Op { get; set; }
        [System.Text.Json.Serialization.JsonConverter(typeof(StringOrPrimitiveJsonConverter))]
        public string? Right { get; set; }
        public List<ConditionRuleGroup>? Groups { get; set; }
    }

    private class LoopStepConfig
    {
        public string? LoopOverStepId { get; set; }
    }

    private class SendEmailStepConfig
    {
        public string? ToAddresses { get; set; }
        public string? Subject { get; set; }
        public string? Body { get; set; }
        public string? CcAddresses { get; set; }
        public string? BccAddresses { get; set; }
        public string? FromAddress { get; set; }
        public List<string>? Attachments { get; set; }
    }

    private class HandleErrorsStepConfig
    {
        public string? FallbackAction { get; set; }
    }

    private static bool IsControlFlowOrInfrastructureException(Exception ex)
    {
        if (ex is PipelineStopExecutionException ||
            ex is PowerBase.Domain.Exceptions.PipelineWaitException ||
            ex is OperationCanceledException ||
            ex is PipelineRecursionException)
        {
            return true;
        }

        var property = ex.GetType().GetProperty("Number");
        if (property != null)
        {
            var number = property.GetValue(ex);
            if (number is int intVal && intVal == 1205)
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsCatchablePipelineStepError(Exception ex)
    {
        if (ex == null) return false;

        if (IsControlFlowOrInfrastructureException(ex))
        {
            return false;
        }

        if (ex is PipelineStopExecutionException ||
            ex is OperationCanceledException ||
            ex is PipelineRunRunningException ||
            ex is PowerBase.Domain.Exceptions.PipelineRecursionException ||
            ex is PowerBase.Domain.Exceptions.PipelineNonRetryableException)
        {
            return false;
        }

        var curr = ex.InnerException;
        while (curr != null)
        {
            if (curr is PipelineStopExecutionException ||
                curr is OperationCanceledException ||
                curr is PipelineRunRunningException ||
                curr is PowerBase.Domain.Exceptions.PipelineRecursionException ||
                curr is PowerBase.Domain.Exceptions.PipelineNonRetryableException)
            {
                return false;
            }
            curr = curr.InnerException;
        }

        if (ex is PowerBase.Domain.Exceptions.NotFoundException ||
            ex is PowerBase.Domain.Exceptions.ValidationException ||
            ex is PowerBase.Domain.Exceptions.UnauthorizedActionException ||
            ex is InvalidOperationException ||
            ex is ArgumentException ||
            ex is FormatException ||
            ex is KeyNotFoundException)
        {
            return true;
        }

        if (ex is System.Net.Http.HttpRequestException httpEx)
        {
            if (httpEx.InnerException is System.Net.Sockets.SocketException)
            {
                return false;
            }

            if (httpEx.StatusCode.HasValue)
            {
                var code = (int)httpEx.StatusCode.Value;
                if (code == 400 || code == 401 || code == 403 || code == 404 || code == 409)
                {
                    return true;
                }
                return false;
            }

            var msg = httpEx.Message;
            if (msg.Contains("status code 5") || msg.Contains("status code 429"))
            {
                return false;
            }

            if (msg.Contains("status code 400") || msg.Contains("status code 401") ||
                msg.Contains("status code 403") || msg.Contains("status code 404") ||
                msg.Contains("status code 409"))
            {
                return true;
            }

            return false;
        }

        return false;
    }

    public static string SanitizeErrorMessage(string? msg)
    {
        if (string.IsNullOrWhiteSpace(msg)) return "An error occurred during step execution.";
        var sanitized = msg;
        if (sanitized.Contains("Bearer ", StringComparison.OrdinalIgnoreCase))
            sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized, @"Bearer\s+[A-Za-z0-9\-\._~\+\/]+=*", "Bearer [REDACTED]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (sanitized.Contains("Password=", StringComparison.OrdinalIgnoreCase))
            sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized, @"Password=[^;]+", "Password=[REDACTED]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return sanitized;
    }


    private class MakeRequestStepConfig
    {
        public string? Url { get; set; }
        public string? Method { get; set; }
        public List<HttpHeader>? HeadersList { get; set; }
        public string? ContentType { get; set; }
        public string? Body { get; set; }
    }

    private class HttpHeader
    {
        public string? Name { get; set; }
        [System.Text.Json.Serialization.JsonConverter(typeof(StringOrPrimitiveJsonConverter))]
        public string? Value { get; set; }
    }

    private class PrepareBulkUpsertConfig
    {
        public List<string>? Fields { get; set; }
        public string? TableLabel { get; set; }
        public string? Table { get; set; }
        public string? TableId { get; set; }
        public string? TablePublicId { get; set; }
        public string? MergeField { get; set; }
        public string? MergeKeyFid { get; set; }
        public string? MergeKeyField { get; set; }
        public string? MergeKey { get; set; }

        public string? GetTableIdentifier() =>
            !string.IsNullOrWhiteSpace(TableLabel) ? TableLabel :
            !string.IsNullOrWhiteSpace(TablePublicId) ? TablePublicId :
            !string.IsNullOrWhiteSpace(TableId) ? TableId :
            !string.IsNullOrWhiteSpace(Table) ? Table : null;

        public string? GetMergeKeyIdentifier() =>
            !string.IsNullOrWhiteSpace(MergeField) ? MergeField :
            !string.IsNullOrWhiteSpace(MergeKeyFid) ? MergeKeyFid :
            !string.IsNullOrWhiteSpace(MergeKeyField) ? MergeKeyField :
            !string.IsNullOrWhiteSpace(MergeKey) ? MergeKey : null;
    }

    private class AddBulkUpsertRowConfig
    {
        public string? ParentUpsertStepRefId { get; set; }
        public string? BulkRecordSetStepId { get; set; }
        public string? ParentStepRefId { get; set; }
        public List<FieldMapping>? FieldMappings { get; set; }
        public Dictionary<string, object?>? RowValues { get; set; }

        public string? GetParentRefId() =>
            !string.IsNullOrWhiteSpace(ParentUpsertStepRefId) ? ParentUpsertStepRefId :
            !string.IsNullOrWhiteSpace(BulkRecordSetStepId) ? BulkRecordSetStepId :
            !string.IsNullOrWhiteSpace(ParentStepRefId) ? ParentStepRefId : null;
    }

    private class CommitBulkUpsertConfig
    {
        public string? ParentUpsertStepRefId { get; set; }
        public string? BulkRecordSetStepId { get; set; }
        public string? ParentStepRefId { get; set; }

        public string? GetParentRefId() =>
            !string.IsNullOrWhiteSpace(ParentUpsertStepRefId) ? ParentUpsertStepRefId :
            !string.IsNullOrWhiteSpace(BulkRecordSetStepId) ? BulkRecordSetStepId :
            !string.IsNullOrWhiteSpace(ParentStepRefId) ? ParentStepRefId : null;
    }

    private class UploadFileStepConfig
    {
        public string? FileUrl { get; set; }
        public string? FileSourceUrl { get; set; }
        public string? FileName { get; set; }
        public string? FileRecordStepId { get; set; }
        public List<SelectedFileField>? SelectedFileFields { get; set; }
        public string? TargetFileField { get; set; }
    }

    private class SelectedFileField
    {
        public string? StepId { get; set; }
        public string? StepLabel { get; set; }
        public string? Field { get; set; }
    }

    public class BulkUpsertSession
    {
        public string TableLabel { get; set; } = string.Empty;
        public string MergeKeyFid { get; set; } = string.Empty;
        public AppTable? Table { get; set; }
        public IReadOnlyList<AppField>? Fields { get; set; }
        public AppField? MergeField { get; set; }
        public HashSet<long>? SelectedFieldFids { get; set; }
        public List<Dictionary<long, object?>> Rows { get; set; } = new();

        public bool IsCommittable { get; set; } = true;
        public BulkUpsertSession? RootSession { get; set; }
        public BulkUpsertSession? DownstreamChildCollection { get; set; }
    }

    private class FieldMapping
    {
        public string? Field { get; set; }
        [System.Text.Json.Serialization.JsonConverter(typeof(StringOrPrimitiveJsonConverter))]
        public string? Value { get; set; }
    }

    private static bool AreValuesEqual(object? val1, object? val2, string? typeCode)
    {
        if (val1 == null && val2 == null) return true;
        if (val1 == null || val2 == null) return false;

        var normalizedCode = (typeCode ?? "TEXT").ToUpperInvariant();

        if (new[] { "NUMERIC", "CURRENCY", "PERCENT", "INTEGER", "FLOAT", "NUMBER", "DECIMAL", "INT" }.Contains(normalizedCode))
        {
            if (decimal.TryParse(val1.ToString(), out var d1) && decimal.TryParse(val2.ToString(), out var d2))
            {
                return d1 == d2;
            }
        }

        if (new[] { "DATE", "DATETIME", "DATE_TIME", "TIME", "TIME_OF_DAY", "TIMESTAMP" }.Contains(normalizedCode))
        {
            if (DateTime.TryParse(val1.ToString(), out var dt1) && DateTime.TryParse(val2.ToString(), out var dt2))
            {
                return dt1 == dt2;
            }
        }

        if (normalizedCode == "BOOLEAN" || normalizedCode == "BOOL" || normalizedCode == "CHECKBOX")
        {
            if (bool.TryParse(val1.ToString(), out var b1) && bool.TryParse(val2.ToString(), out var b2))
            {
                return b1 == b2;
            }
        }

        return string.Equals(val1.ToString()?.Trim(), val2.ToString()?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string MapUiOperatorToDbOperator(string? op)
    {
        if (string.IsNullOrWhiteSpace(op)) return "eq";
        var normalized = op.Trim().ToLowerInvariant().Replace('_', '-');
        return normalized switch
        {
            "is" => "eq",
            "is-not" => "ne",
            "greater-than" => "gt",
            "greater-than-or-equals" => "gte",
            "less-than" => "lt",
            "less-than-or-equals" => "lte",
            "is-after" => "gt",
            "is-on-or-after" => "gte",
            "is-before" => "lt",
            "is-on-or-before" => "lte",
            "contains" => "contains",
            "not-contains" => "notContains",
            "starts-with" => "startsWith",
            "is-empty" => "isEmpty",
            "is-not-empty" => "isNotEmpty",
            "is-true" => "eq",
            "is-false" => "eq",
            _ => normalized
        };
    }

    private FilterGroup? MapTriggerFilterGroupToDbFilterGroup(
        TriggerFilterGroup group,
        IReadOnlyList<AppField> fields,
        string payloadJson,
        string? executionPath = null,
        List<PipelineStep>? allSteps = null)
    {
        if (group?.Rules == null || !group.Rules.Any()) return null;

        var dbGroup = new FilterGroup
        {
            Logic = group.LogicalOp?.ToLowerInvariant() == "or" ? "or" : "and",
            Nodes = new List<FilterNode>()
        };

        foreach (var rule in group.Rules)
        {
            if (rule == null || PipelineFilterEvaluator.IsRuleCompletelyBlank(rule)) continue;

            if (rule.Type == "nested")
            {
                if (rule.Groups != null)
                {
                    foreach (var subGroup in rule.Groups)
                    {
                        var mappedSub = MapTriggerFilterGroupToDbFilterGroup(subGroup, fields, payloadJson, executionPath, allSteps);
                        if (mappedSub != null)
                        {
                            dbGroup.Nodes.Add(new FilterNode { Group = mappedSub });
                        }
                    }
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(rule.Field)) continue;

                var field = ResolvePipelineField(rule.Field, fields);

                if (field.Fid.HasValue)
                {
                    var rawValue = rule.Value;
                    var dbOp = MapUiOperatorToDbOperator(rule.Operator);

                    if (rule.Operator == "is_true" || rule.Operator == "is-true")
                    {
                        rawValue = "true";
                    }
                    else if (rule.Operator == "is_false" || rule.Operator == "is-false")
                    {
                        rawValue = "false";
                    }

                    var evaluatedValue = EvaluateTokens(rawValue, payloadJson, executionPath, allSteps);

                    dbGroup.Nodes.Add(new FilterNode
                    {
                        Condition = new FilterCondition
                        {
                            FieldId = field.Fid.Value,
                            Operator = dbOp,
                            Value = evaluatedValue
                        }
                    });
                }
            }
        }

        return dbGroup.Nodes.Any() ? dbGroup : null;
    }

    private class LookUpRecordStepConfig
    {
        public string? ConnectionPublicId { get; set; }
        public string? AppPublicId { get; set; }
        public string? TablePublicId { get; set; }
        public List<string>? SubsequentFields { get; set; }
        public string? CompareLocalTime { get; set; }
        [System.Text.Json.Serialization.JsonConverter(typeof(StringOrPrimitiveJsonConverter))]
        public string? RecordIdValue { get; set; }
    }

    private class SearchRecordsStepConfig
    {
        public string? TableId { get; set; }
        public string? FilterField { get; set; }
        [System.Text.Json.Serialization.JsonConverter(typeof(StringOrPrimitiveJsonConverter))]
        public string? FilterValue { get; set; }
        [System.Text.Json.Serialization.JsonConverter(typeof(NullableIntJsonConverter))]
        public int? MaxResults { get; set; }
        public List<TriggerFilterRule>? Filters { get; set; }
        public List<TriggerFilterGroup>? FilterGroups { get; set; }
    }

    private bool IsSqlDeadlock(Exception ex)
    {
        var property = ex.GetType().GetProperty("Number");
        if (property != null)
        {
            var number = property.GetValue(ex);
            if (number is int intVal && intVal == 1205)
            {
                return true;
            }
        }
        return false;
    }

    private class StringOrPrimitiveJsonConverter : System.Text.Json.Serialization.JsonConverter<string?>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    return reader.GetString();
                case JsonTokenType.Number:
                    using (var doc = JsonDocument.ParseValue(ref reader))
                    {
                        return doc.RootElement.GetRawText();
                    }
                case JsonTokenType.True:
                    return "true";
                case JsonTokenType.False:
                    return "false";
                case JsonTokenType.Null:
                    return null;
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    throw new JsonException($"Unsupported complex JSON token type '{reader.TokenType}'. Object and Array configurations are not supported for this field mapping.");
                default:
                    using (var doc = JsonDocument.ParseValue(ref reader))
                    {
                        return doc.RootElement.GetRawText();
                    }
            }
        }

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        {
            if (value == null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStringValue(value);
            }
        }
    }

    private class NullableIntJsonConverter : System.Text.Json.Serialization.JsonConverter<int?>
    {
        public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Number:
                    if (reader.TryGetInt32(out var val))
                    {
                        if (val <= 0) return null;
                        return val;
                    }
                    return null;
                case JsonTokenType.String:
                    var str = reader.GetString();
                    if (string.IsNullOrWhiteSpace(str)) return null;
                    if (str.Equals("unlimited", StringComparison.OrdinalIgnoreCase)) return null;
                    if (int.TryParse(str, out var intVal))
                    {
                        if (intVal <= 0) return null;
                        return intVal;
                    }
                    return null;
                case JsonTokenType.Null:
                    return null;
                default:
                    return null;
            }
        }

        public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
        {
            if (value.HasValue)
            {
                writer.WriteNumberValue(value.Value);
            }
            else
            {
                writer.WriteNullValue();
            }
        }
    }

    private static string SerializeAndSanitizeAudit(object? obj, int maxChars = 32000)
    {
        if (obj == null) return "{}";
        try
        {
            var sanitizedObj = SanitizeObject(obj);
            var json = JsonSerializer.Serialize(sanitizedObj);
            if (json.Length <= maxChars)
            {
                return json;
            }

            var truncatedObj = TruncateLargeProperties(sanitizedObj);
            json = JsonSerializer.Serialize(truncatedObj);
            if (json.Length <= maxChars)
            {
                return json;
            }

            return JsonSerializer.Serialize(new
            {
                Truncated = true,
                OriginalLength = json.Length,
                Message = "Payload too large to log fully."
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { Error = "Failed to serialize audit context.", Details = ex.Message });
        }
    }

    private static string SerializeAndSanitizeAuditString(string? jsonStr, int maxChars = 32000)
    {
        if (string.IsNullOrWhiteSpace(jsonStr)) return "{}";
        try
        {
            using var doc = JsonDocument.Parse(jsonStr);
            var sanitizedObj = SanitizeJsonElement(doc.RootElement);
            var json = JsonSerializer.Serialize(sanitizedObj);
            if (json.Length <= maxChars)
            {
                return json;
            }

            var truncatedObj = TruncateLargeProperties(sanitizedObj);
            json = JsonSerializer.Serialize(truncatedObj);
            if (json.Length <= maxChars)
            {
                return json;
            }

            return JsonSerializer.Serialize(new
            {
                Truncated = true,
                OriginalLength = json.Length,
                Message = "Payload too large to log fully."
            });
        }
        catch
        {
            var safeVal = jsonStr ?? string.Empty;
            if (safeVal.Length > maxChars)
            {
                safeVal = safeVal.Substring(0, maxChars) + "... [TRUNCATED]";
            }
            return JsonSerializer.Serialize(new { Truncated = true, PlainTextPreview = safeVal });
        }
    }

    private static object? SanitizeObject(object? obj)
    {
        if (obj == null) return null;

        if (obj is string str)
        {
            return str;
        }

        if (obj is IDictionary<string, object?> dictStr)
        {
            var newDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in dictStr)
            {
                if (IsSensitiveKey(kvp.Key))
                {
                    newDict[kvp.Key] = "[REDACTED]";
                }
                else
                {
                    newDict[kvp.Key] = SanitizeObject(kvp.Value);
                }
            }
            return newDict;
        }

        if (obj is IDictionary<long, object?> dictLong)
        {
            var newDict = new Dictionary<long, object?>();
            foreach (var kvp in dictLong)
            {
                newDict[kvp.Key] = SanitizeObject(kvp.Value);
            }
            return newDict;
        }

        if (obj is System.Collections.IEnumerable enumerable && obj is not string)
        {
            var newList = new List<object?>();
            foreach (var item in enumerable)
            {
                newList.Add(SanitizeObject(item));
            }
            return newList;
        }

        var type = obj.GetType();
        if (type.IsClass && type != typeof(string))
        {
            var newDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in type.GetProperties())
            {
                if (!prop.CanRead) continue;
                try
                {
                    var val = prop.GetValue(obj);
                    if (IsSensitiveKey(prop.Name))
                    {
                        newDict[prop.Name] = "[REDACTED]";
                    }
                    else
                    {
                        newDict[prop.Name] = SanitizeObject(val);
                    }
                }
                catch
                {
                    // Ignore properties that fail to read
                }
            }
            return newDict;
        }

        return obj;
    }

    private static bool IsSensitiveKey(string key)
    {
        var sensitiveKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "authorization", "proxy-authorization", "cookie", "set-cookie", 
            "password", "passwd", "token", "access_token", "refresh_token", 
            "api-key", "api_key", "x-api-key", "client-secret", "client_secret", "secret"
        };
        return sensitiveKeys.Any(sk => key.Contains(sk, StringComparison.OrdinalIgnoreCase));
    }

    private static object? TruncateLargeProperties(object? obj)
    {
        if (obj == null) return null;

        if (obj is string str)
        {
            if (str.Length > 2000)
            {
                return str.Substring(0, 2000) + "... [TRUNCATED]";
            }
            return str;
        }

        if (obj is IDictionary<string, object?> dictStr)
        {
            var newDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in dictStr)
            {
                newDict[kvp.Key] = TruncateLargeProperties(kvp.Value);
            }
            return newDict;
        }

        if (obj is IDictionary<long, object?> dictLong)
        {
            var newDict = new Dictionary<long, object?>();
            foreach (var kvp in dictLong)
            {
                newDict[kvp.Key] = TruncateLargeProperties(kvp.Value);
            }
            return newDict;
        }

        if (obj is System.Collections.IEnumerable enumerable && obj is not string)
        {
            var list = new List<object?>();
            int count = 0;
            foreach (var item in enumerable)
            {
                if (count > 50)
                {
                    list.Add("... [ADDITIONAL ITEMS TRUNCATED]");
                    break;
                }
                list.Add(TruncateLargeProperties(item));
                count++;
            }
            return list;
        }

        return obj;
    }

    private static object? SanitizeJsonElement(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in el.EnumerateObject())
                {
                    if (IsSensitiveKey(prop.Name))
                    {
                        dict[prop.Name] = "[REDACTED]";
                    }
                    else
                    {
                        dict[prop.Name] = SanitizeJsonElement(prop.Value);
                    }
                }
                return dict;
            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in el.EnumerateArray())
                {
                    list.Add(SanitizeJsonElement(item));
                }
                return list;
            case JsonValueKind.String:
                return el.GetString();
            case JsonValueKind.Number:
                if (el.TryGetInt64(out var l)) return l;
                return el.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null:
                return null;
            default:
                return el.GetRawText();
        }
    }

    private static AppField ResolvePipelineField(
        string? fieldReference,
        IReadOnlyList<AppField> fields)
    {
        if (string.IsNullOrWhiteSpace(fieldReference))
        {
            throw new PowerBase.Domain.Exceptions.PipelineNonRetryableException("Field reference cannot be null or empty.");
        }

        if (fieldReference.StartsWith("fid_", StringComparison.OrdinalIgnoreCase))
        {
            var fidStr = fieldReference.Substring(4);
            if (int.TryParse(fidStr, out var fid))
            {
                var field = fields.FirstOrDefault(f => f.Fid == fid);
                if (field != null)
                {
                    return field;
                }
            }
            throw new PowerBase.Domain.Exceptions.PipelineNonRetryableException(
                $"Field with stable FID '{fidStr}' was not found in the target table.");
        }
        else if (fieldReference.StartsWith("f_", StringComparison.OrdinalIgnoreCase))
        {
            var fidStr = fieldReference.Substring(2);
            if (int.TryParse(fidStr, out var fid))
            {
                var field = fields.FirstOrDefault(f => f.Fid == fid);
                if (field != null)
                {
                    return field;
                }
            }
            throw new PowerBase.Domain.Exceptions.PipelineNonRetryableException(
                $"Field with stable FID '{fidStr}' was not found in the target table.");
        }
        else
        {
            var field = fields.FirstOrDefault(f => f.Name.Equals(fieldReference, StringComparison.OrdinalIgnoreCase));
            if (field != null)
            {
                return field;
            }
            throw new PowerBase.Domain.Exceptions.PipelineNonRetryableException(
                $"Field with name '{fieldReference}' was not found in the target table.");
        }
    }

    private static AppField ResolveBulkUpsertField(
        string? fieldReference,
        IReadOnlyList<AppField> fields)
    {
        if (string.IsNullOrWhiteSpace(fieldReference))
        {
            throw new PowerBase.Domain.Exceptions.PipelineNonRetryableException("Field reference cannot be null or empty.");
        }

        if (fieldReference.StartsWith("fid_", StringComparison.OrdinalIgnoreCase))
        {
            var fidStr = fieldReference.Substring(4);
            if (int.TryParse(fidStr, out var fid))
            {
                var field = fields.FirstOrDefault(f => f.Fid == fid);
                if (field != null) return field;
            }
            throw new PowerBase.Domain.Exceptions.PipelineNonRetryableException(
                $"Field with stable FID '{fidStr}' was not found in the target table.");
        }
        else if (fieldReference.StartsWith("f_", StringComparison.OrdinalIgnoreCase))
        {
            var fidStr = fieldReference.Substring(2);
            if (int.TryParse(fidStr, out var fid))
            {
                var field = fields.FirstOrDefault(f => f.Fid == fid);
                if (field != null) return field;
            }
            throw new PowerBase.Domain.Exceptions.PipelineNonRetryableException(
                $"Field with stable FID '{fidStr}' was not found in the target table.");
        }
        else if (int.TryParse(fieldReference, out var directFid))
        {
            var field = fields.FirstOrDefault(f => f.Fid == directFid);
            if (field != null) return field;
            var fieldByName = fields.FirstOrDefault(f => string.Equals(f.Name, fieldReference, StringComparison.OrdinalIgnoreCase));
            if (fieldByName != null) return fieldByName;

            throw new PowerBase.Domain.Exceptions.PipelineNonRetryableException(
                $"Field with stable FID '{fieldReference}' was not found in the target table.");
        }
        else
        {
            var field = fields.FirstOrDefault(f =>
                string.Equals(f.Name, fieldReference, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(f.Label, fieldReference, StringComparison.OrdinalIgnoreCase) ||
                (f.IsSystem && string.Equals(f.PhysicalColumnName, fieldReference, StringComparison.OrdinalIgnoreCase)));
            if (field != null)
            {
                return field;
            }
            throw new PowerBase.Domain.Exceptions.PipelineNonRetryableException(
                $"Field with name '{fieldReference}' was not found in the target table.");
        }
    }

    public class RawStepAuditSnapshot
    {
        public PipelineStep Step { get; set; } = null!;
        public PipelineStepRun StepRun { get; set; } = null!;
        public string? RawInputJson { get; set; }
        public string? RawOutputJson { get; set; }
        public string Status { get; set; } = null!;
        public DateTime StartedOn { get; set; }
        public DateTime CompletedOn { get; set; }
        public bool RolledBack { get; set; }
    }
}

public class PipelineBulkUpsertException : InvalidOperationException
{
    public string ErrorCode { get; }
    public int? RowIndex { get; }
    public int? FieldFid { get; }
    public string? FieldName { get; }
    public bool IsRetryable { get; }

    public PipelineBulkUpsertException(
        string errorCode,
        string message,
        int? rowIndex = null,
        int? fieldFid = null,
        string? fieldName = null,
        bool isRetryable = false)
        : base(message)
    {
        ErrorCode = errorCode;
        RowIndex = rowIndex;
        FieldFid = fieldFid;
        FieldName = fieldName;
        IsRetryable = isRetryable;
    }
}
