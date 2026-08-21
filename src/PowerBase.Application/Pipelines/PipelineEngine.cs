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
                    if (run.Status == "Success" || run.Status == "Skipped")
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
                throw new PipelineRecursionException($"Pipeline recursion limit exceeded. Depth is {task.Depth}. CorrelationId: {task.CorrelationId}");
            }

            // Fetch steps
            var steps = await _pipelineRepo.GetStepsByPipelineIdAsync(task.PipelineId, ct);
            var activeSteps = steps.Where(s => !s.IsDeleted).ToList();

            await _auditFormatter.InitializeAsync(task.PipelineId, task.TriggeredBy, ct);

            var pipelineMeta = await _pipelineRepo.GetByIdAsync(task.PipelineId, ct);
            var triggerStep = activeSteps.FirstOrDefault(s => s.Type == "trigger" && s.Subtype == "new-event");

            bool isSkipped = pipelineMeta == null || !pipelineMeta.IsActive || pipelineMeta.IsDeleted || triggerStep == null;
            if (isSkipped)
            {
                using (var suppressScope = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
                {
                    run.Status = "Skipped";
                    run.ErrorMessage = "Skipped: Trigger configuration is inactive or deleted.";
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
                        LastError = "Skipped: Trigger configuration is inactive or deleted."
                    }, ct);
                    suppressScope.Complete();
                }
                return;
            }

            _logger.LogInformation("Pipeline {PipelineId} (Attempt {Attempt}) executing active steps.", task.PipelineId, run.AttemptCount);

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
                                            stepsDict[refId] = selectedDict;
                                        }
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
            .Where(s => s.ParentStepId == parentStepId && (parentStepId == null || s.ParentBranch == parentBranch))
            .OrderBy(s => s.DisplayOrder)
            .ToList();

        foreach (var step in siblings)
        {
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
            var currentPath = $"{executionPath}/{step.RefId}";

            try
            {
                var contextJson = JsonSerializer.Serialize(contextDict);
                var output = await ExecuteStepAsync(step, contextJson, contextDict, allSteps, stepsDict, runId, stepRun, snapshots, currentPath, ct);

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

        long createdBy = contextDict.TryGetValue("_CreatedBy", out var cbObj) && cbObj is long cbVal ? cbVal : 0L;
        Guid messageGuid = contextDict.TryGetValue("_MessageId", out var msgObj) && msgObj is Guid msgGuid ? msgGuid : Guid.Empty;

        var ownerTenantId = _queryContext.TenantId;
        var targetTenantId = ownerTenantId;
        bool isCrossTenant = false;

        if (Guid.TryParse(connectionPublicId, out var connectionGuid) && !PipelineStepValidator.SystemConnectionIds.Contains(connectionGuid))
        {
            var resolvedTenantId = await _adminRepo.GetTenantIdByPublicIdAsync(connectionGuid, ct);
            if (resolvedTenantId.HasValue)
            {
                targetTenantId = resolvedTenantId.Value;
                isCrossTenant = targetTenantId != ownerTenantId;
            }
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

                var scopedTenantRepo = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
                var isMember = await scopedTenantRepo.IsActiveMemberAsync(createdBy, ct);
                if (!isMember)
                {
                    throw new UnauthorizedAccessException($"Execution authority user {createdBy} is not an active member of target tenant {targetTenantId}.");
                }

                var scopedRecordRepo = scope.ServiceProvider.GetRequiredService<IRecordRepository>();
                var scopedTableRepo = scope.ServiceProvider.GetRequiredService<IAppTableRepository>();
                var scopedFieldRepo = scope.ServiceProvider.GetRequiredService<IAppFieldRepository>();
                var scopedWriteService = scope.ServiceProvider.GetRequiredService<IRecordWriteService>();
                var scopedTriggerInterceptor = scope.ServiceProvider.GetRequiredService<IPipelineTriggerInterceptor>();
                var scopedUow = scope.ServiceProvider.GetRequiredService<ITenantUnitOfWork>();
                var scopedIdempotencyRepo = scope.ServiceProvider.GetRequiredService<IPipelineStepIdempotencyRepository>();
                var scopedFileStorage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();

                return await ExecuteStepWithServicesAsync(step, payloadJson, contextDict, allSteps, stepsDict, runId, stepRun, snapshots, executionPath,
                    scopedRecordRepo, scopedTableRepo, scopedFieldRepo, scopedWriteService, scopedTriggerInterceptor, scopedUow, scopedIdempotencyRepo, scopedFileStorage, ct);
            }
        }
        else
        {
            return await ExecuteStepWithServicesAsync(step, payloadJson, contextDict, allSteps, stepsDict, runId, stepRun, snapshots, executionPath,
                _recordRepo, _tableRepo, _fieldRepo, _recordWriteService, _triggerInterceptor, _uow, _idempotencyRepo, _fileStorageService, ct);
        }
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

        if (subtype == "search-records")
        {
            var config = JsonSerializer.Deserialize<SearchRecordsStepConfig>(step.ConfigJson ?? "{}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (config == null || string.IsNullOrWhiteSpace(config.TableId))
                throw new InvalidOperationException("Search records step configuration is invalid or missing tableId.");

            var tableGuid = Guid.Parse(config.TableId);
            var table = await tableRepo.GetByPublicIdAsync(tableGuid, ct);
            var fields = await fieldRepo.ListByTableAsync(table.Id, ct);

            FilterGroup? filterTree = null;
            string? evaluatedFilterVal = null;
            if (!string.IsNullOrWhiteSpace(config.FilterField))
            {
                var field = fields.FirstOrDefault(f => 
                    f.Name.Equals(config.FilterField, StringComparison.OrdinalIgnoreCase) || 
                    $"fid_{f.Id}".Equals(config.FilterField, StringComparison.OrdinalIgnoreCase) ||
                    $"fid_{f.Fid}".Equals(config.FilterField, StringComparison.OrdinalIgnoreCase));

                if (field != null && field.Fid.HasValue)
                {
                    evaluatedFilterVal = EvaluateTokens(config.FilterValue, payloadJson);
                    var filterCondition = new FilterCondition
                    {
                        FieldId = field.Id,
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

            var limit = config.MaxResults ?? 10;

            stepRun.InputContext = SerializeAndSanitizeAudit(new {
                TableId = config.TableId,
                FilterField = config.FilterField,
                FilterValue = evaluatedFilterVal,
                MaxResults = limit
            });

            var records = await recordRepo.ListAsync(table, fields, page: 1, pageSize: limit, filterTree: filterTree, ct: ct);
            var resultsList = records?.ToList() ?? new List<IReadOnlyDictionary<string, object?>>();

            var stepOutput = new { records = resultsList };
            return JsonSerializer.Serialize(stepOutput);
        }
        else if (subtype == "create-record")
        {
            var config = JsonSerializer.Deserialize<CreateRecordStepConfig>(step.ConfigJson ?? "{}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (config == null || string.IsNullOrWhiteSpace(config.TableId))
                throw new InvalidOperationException("Create record step configuration is invalid or missing tableId.");

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

                    var field = fields.FirstOrDefault(f => 
                        f.Name.Equals(mapping.Field, StringComparison.OrdinalIgnoreCase) || 
                        $"fid_{f.Id}".Equals(mapping.Field, StringComparison.OrdinalIgnoreCase) ||
                        $"fid_{f.Fid}".Equals(mapping.Field, StringComparison.OrdinalIgnoreCase));

                    if (field != null && field.Fid.HasValue)
                    {
                        var resolvedValStr = EvaluateTokens(mapping.Value, payloadJson);
                        var parsedVal = ParseValueType(resolvedValStr, field.TypeCode);
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
                var recordId = await recordRepo.GetRecordIdByPublicIdAsync(table, recordPublicId, uow.Transaction, ct);
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
                await uow.RollbackAsync(ct);
                var winningOutput = await idempotencyRepo.GetByExecutionKeyAsync(messageGuid, step.PublicId, executionPathHash, null, ct);
                if (winningOutput != null) return winningOutput;
                throw;
            }
            catch
            {
                await uow.RollbackAsync(ct);
                throw;
            }
        }
        else if (subtype == "update-record")
        {
            var config = JsonSerializer.Deserialize<UpdateRecordStepConfig>(step.ConfigJson ?? "{}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (config == null || string.IsNullOrWhiteSpace(config.TableId))
                throw new InvalidOperationException("Update record step configuration is invalid or missing tableId.");

            var resolvedRecordIdStr = EvaluateTokens(config.TargetRecordId, payloadJson);
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

                    var field = fields.FirstOrDefault(f => 
                        f.Name.Equals(mapping.Field, StringComparison.OrdinalIgnoreCase) || 
                        $"fid_{f.Id}".Equals(mapping.Field, StringComparison.OrdinalIgnoreCase) ||
                        $"fid_{f.Fid}".Equals(mapping.Field, StringComparison.OrdinalIgnoreCase));

                    if (field != null && field.Fid.HasValue)
                    {
                        var resolvedValStr = EvaluateTokens(mapping.Value, payloadJson);
                        var parsedVal = ParseValueType(resolvedValStr, field.TypeCode);
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
                await uow.RollbackAsync(ct);
                var winningOutput = await idempotencyRepo.GetByExecutionKeyAsync(messageGuid, step.PublicId, executionPathHash, null, ct);
                if (winningOutput != null) return winningOutput;
                throw;
            }
            catch
            {
                await uow.RollbackAsync(ct);
                throw;
            }
        }
        else if (subtype == "delete-record")
        {
            var config = JsonSerializer.Deserialize<DeleteRecordStepConfig>(step.ConfigJson ?? "{}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (config == null || string.IsNullOrWhiteSpace(config.TableId) || string.IsNullOrWhiteSpace(config.TargetRecordId))
                throw new InvalidOperationException("Delete record step configuration is invalid or missing tableId/targetRecordId.");

            var resolvedRecordIdStr = EvaluateTokens(config.TargetRecordId, payloadJson);
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
                await uow.RollbackAsync(ct);
                var winningOutput = await idempotencyRepo.GetByExecutionKeyAsync(messageGuid, step.PublicId, executionPathHash, null, ct);
                if (winningOutput != null) return winningOutput;
                throw;
            }
            catch
            {
                await uow.RollbackAsync(ct);
                throw;
            }
        }
        else if (subtype == "stop")
        {
            var config = JsonSerializer.Deserialize<StopStepConfig>(step.ConfigJson ?? "{}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var reason = EvaluateTokens(config?.Reason, payloadJson);

            stepRun.InputContext = SerializeAndSanitizeAudit(new {
                Reason = reason
            });

            throw new PipelineStopExecutionException(string.IsNullOrWhiteSpace(reason) ? "Execution halted by pipeline stop action." : reason);
        }
        else if (subtype == "condition")
        {
            var config = JsonSerializer.Deserialize<ConditionStepConfig>(step.ConfigJson ?? "{}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            
            bool isMatched = false;
            if (config?.RuleGroups != null && config.RuleGroups.Any())
            {
                isMatched = config.RuleGroups.All(g => EvaluateConditionGroup(g, payloadJson));
                _logger.LogInformation("Condition step {StepId} resolved via recursive ruleGroups evaluation. Result: {Result}", step.Id, isMatched);

                stepRun.InputContext = SerializeAndSanitizeAudit(new {
                    RuleGroups = config.RuleGroups.Select(g => new {
                        g.LogicalOp,
                        Rules = g.Rules?.Select(r => new {
                            r.Type,
                            Left = EvaluateTokens(r.Left, payloadJson),
                            Op = r.Op,
                            Right = EvaluateTokens(r.Right, payloadJson)
                        })
                    })
                });
            }
            else
            {
                var left = EvaluateTokens(config?.LeftOperand, payloadJson);
                var right = EvaluateTokens(config?.RightOperand, payloadJson);
                var op = config?.Operator ?? "equals";
                isMatched = EvaluateConditionOperator(left, op, right);
                _logger.LogInformation("Condition step {StepId} resolved Left: '{Left}', Operator: '{Op}', Right: '{Right}'. Result: {Result}", step.Id, left, op, right, isMatched);

                stepRun.InputContext = SerializeAndSanitizeAudit(new {
                    LeftOperand = left,
                    Operator = op,
                    RightOperand = right
                });
            }

            var branch = isMatched ? "children" : "elsechildren";
            await ExecuteSiblingStepsAsync(runId, allSteps, step.Id, branch, contextDict, stepsDict, snapshots, $"{executionPath}/{branch}", ct);

            return JsonSerializer.Serialize(new { Matched = isMatched, EvaluatedBranch = branch });
        }
        else if (subtype == "loop")
        {
            var config = JsonSerializer.Deserialize<LoopStepConfig>(step.ConfigJson ?? "{}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (config == null || string.IsNullOrWhiteSpace(config.LoopOverStepId))
                throw new InvalidOperationException("Loop step configuration is invalid or missing LoopOverStepId.");

            stepsDict.TryGetValue(config.LoopOverStepId, out var listObj);
            var items = GetLoopCollection(listObj);
            var itemsList = items?.ToList() ?? new List<object>();

            stepRun.InputContext = SerializeAndSanitizeAudit(new {
                LoopOverStepId = config.LoopOverStepId,
                ItemCount = itemsList.Count
            });

            _logger.LogInformation("Loop step {StepId} iterating over step '{LoopOverStepId}' containing {Count} items.", step.Id, config.LoopOverStepId, itemsList.Count);

            int index = 0;
            int count = itemsList.Count;

            stepsDict.TryGetValue(step.RefId, out var previousLoopScope);

            foreach (var item in itemsList)
            {
                var loopScope = new Dictionary<string, object>
                {
                    { "item", item },
                    { "index", index },
                    { "is_first", index == 0 },
                    { "is_last", index == count - 1 }
                };

                stepsDict[step.RefId] = loopScope;

                await ExecuteSiblingStepsAsync(runId, allSteps, step.Id, "children", contextDict, stepsDict, snapshots, $"{executionPath}/loop_index_{index}", ct);

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

            return JsonSerializer.Serialize(new { LoopCompleted = true, IterationCount = count });
        }
        else if (subtype == "send-email" || subtype == "send-email-outlook")
        {
            var config = JsonSerializer.Deserialize<SendEmailStepConfig>(step.ConfigJson ?? "{}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (config == null || string.IsNullOrWhiteSpace(config.ToAddresses))
                throw new InvalidOperationException("Send email step configuration is invalid or missing ToAddresses.");

            var resolvedTo = EvaluateTokens(config.ToAddresses, payloadJson);
            var resolvedSubject = EvaluateTokens(config.Subject, payloadJson);
            var resolvedBody = EvaluateTokens(config.Body, payloadJson);
            var resolvedCc = EvaluateTokens(config.CcAddresses, payloadJson);
            var resolvedBcc = EvaluateTokens(config.BccAddresses, payloadJson);
            var resolvedFrom = EvaluateTokens(config.FromAddress, payloadJson);

            List<string>? resolvedAttachments = null;
            if (config.Attachments != null && config.Attachments.Count > 0)
            {
                resolvedAttachments = new List<string>();
                foreach (var path in config.Attachments)
                {
                    var resolvedPath = EvaluateTokens(path, payloadJson);
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
            var config = JsonSerializer.Deserialize<MakeRequestStepConfig>(step.ConfigJson ?? "{}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (config == null || string.IsNullOrWhiteSpace(config.Url))
                throw new InvalidOperationException("Make request step configuration is invalid or missing URL.");

            var resolvedUrl = EvaluateTokens(config.Url, payloadJson);
            var method = string.IsNullOrWhiteSpace(config.Method) ? "GET" : config.Method.ToUpperInvariant();

            var client = _httpClientFactory.CreateClient();
            var request = new HttpRequestMessage(new HttpMethod(method), resolvedUrl);

            var correlationId = contextDict.TryGetValue("_CorrelationId", out var corrObj) ? corrObj?.ToString() : null;
            var depth = contextDict.TryGetValue("_Depth", out var dObj) && dObj is int dVal ? dVal : 1;

            if (!string.IsNullOrEmpty(correlationId))
            {
                request.Headers.TryAddWithoutValidation("X-PowerBase-Correlation-Id", correlationId);
            }
            request.Headers.TryAddWithoutValidation("X-PowerBase-Depth", (depth + 1).ToString());

            var resolvedHeaders = new List<HttpHeader>();
            if (config.HeadersList != null)
            {
                foreach (var header in config.HeadersList)
                {
                    if (string.IsNullOrWhiteSpace(header.Name)) continue;
                    var resolvedVal = EvaluateTokens(header.Value, payloadJson);
                    request.Headers.TryAddWithoutValidation(header.Name, resolvedVal);
                    resolvedHeaders.Add(new HttpHeader { Name = header.Name, Value = resolvedVal });
                }
            }

            string? resolvedBody = null;
            if (method == "POST" || method == "PUT" || method == "PATCH")
            {
                resolvedBody = EvaluateTokens(config.Body, payloadJson);
                var contentType = string.IsNullOrWhiteSpace(config.ContentType) ? "application/json" : config.ContentType;
                request.Content = new StringContent(resolvedBody, System.Text.Encoding.UTF8, contentType);
            }

            stepRun.InputContext = SerializeAndSanitizeAudit(new {
                Url = resolvedUrl,
                Method = method,
                Headers = resolvedHeaders,
                ContentType = config.ContentType,
                Body = resolvedBody
            });

            var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException($"HTTP Request failed with status code {response.StatusCode}: {errorContent}");
            }

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            return responseBody;
        }
        else if (subtype == "prepare-bulk-upsert")
        {
            var config = JsonSerializer.Deserialize<PrepareBulkUpsertConfig>(step.ConfigJson ?? "{}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (config == null || string.IsNullOrWhiteSpace(config.TableLabel) || string.IsNullOrWhiteSpace(config.MergeKeyFid))
                throw new InvalidOperationException("Prepare bulk upsert step configuration is invalid or missing table/merge key.");

            stepRun.InputContext = SerializeAndSanitizeAudit(new {
                TableLabel = config.TableLabel,
                MergeKeyFid = config.MergeKeyFid
            });

            var session = new BulkUpsertSession
            {
                TableLabel = config.TableLabel,
                MergeKeyFid = config.MergeKeyFid,
                Rows = new List<Dictionary<long, object?>>()
            };

            var sessions = GetOrCreateBulkUpsertSessions(contextDict);
            sessions[step.RefId] = session;

            return JsonSerializer.Serialize(new { SessionId = step.RefId, Status = "Prepared" });
        }
        else if (subtype == "add-bulk-upsert-row")
        {
            var config = JsonSerializer.Deserialize<AddBulkUpsertRowConfig>(step.ConfigJson ?? "{}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (config == null || string.IsNullOrWhiteSpace(config.ParentUpsertStepRefId))
                throw new InvalidOperationException("Add bulk upsert row step configuration is invalid or missing ParentUpsertStepRefId.");

            var parentRefId = config.ParentUpsertStepRefId.Replace("steps.", "");
            var sessions = GetOrCreateBulkUpsertSessions(contextDict);
            if (!sessions.TryGetValue(parentRefId, out var session))
                throw new InvalidOperationException($"No bulk upsert session found for parent reference: '{config.ParentUpsertStepRefId}'");

            var tableGuid = Guid.Parse(session.TableLabel);
            var table = await tableRepo.GetByPublicIdAsync(tableGuid, ct);
            var fields = await fieldRepo.ListByTableAsync(table.Id, ct);

            var rowValues = new Dictionary<long, object?>();
            var resolvedMappings = new Dictionary<string, object?>();
            if (config.FieldMappings != null)
            {
                foreach (var mapping in config.FieldMappings)
                {
                    if (string.IsNullOrWhiteSpace(mapping.Field)) continue;

                    var field = fields.FirstOrDefault(f => 
                        f.Name.Equals(mapping.Field, StringComparison.OrdinalIgnoreCase) || 
                        $"fid_{f.Id}".Equals(mapping.Field, StringComparison.OrdinalIgnoreCase) ||
                        $"fid_{f.Fid}".Equals(mapping.Field, StringComparison.OrdinalIgnoreCase));

                    if (field != null && field.Fid.HasValue)
                    {
                        var resolvedValStr = EvaluateTokens(mapping.Value, payloadJson);
                        var parsedVal = ParseValueType(resolvedValStr, field.TypeCode);
                        rowValues[field.Fid.Value] = parsedVal;
                        resolvedMappings[mapping.Field] = parsedVal;
                    }
                }
            }

            stepRun.InputContext = SerializeAndSanitizeAudit(new {
                ParentUpsertStepRefId = config.ParentUpsertStepRefId,
                FieldMappings = resolvedMappings
            });

            session.Rows.Add(rowValues);
            return JsonSerializer.Serialize(new { Status = "RowAdded", RowCount = session.Rows.Count });
        }
        else if (subtype == "commit-upsert")
        {
            var config = JsonSerializer.Deserialize<CommitBulkUpsertConfig>(step.ConfigJson ?? "{}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (config == null || string.IsNullOrWhiteSpace(config.ParentUpsertStepRefId))
                throw new InvalidOperationException("Commit bulk upsert step configuration is invalid or missing ParentUpsertStepRefId.");

            stepRun.InputContext = SerializeAndSanitizeAudit(new {
                ParentUpsertStepRefId = config.ParentUpsertStepRefId
            });

            var parentRefId = config.ParentUpsertStepRefId.Replace("steps.", "");
            var sessions = GetOrCreateBulkUpsertSessions(contextDict);
            if (!sessions.TryGetValue(parentRefId, out var session))
                throw new InvalidOperationException($"No bulk upsert session found for parent reference: '{config.ParentUpsertStepRefId}'");

            var tableGuid = Guid.Parse(session.TableLabel);
            var table = await tableRepo.GetByPublicIdAsync(tableGuid, ct);
            var fields = await fieldRepo.ListByTableAsync(table.Id, ct);

            var mergeField = fields.FirstOrDefault(f => 
                f.Name.Equals(session.MergeKeyFid, StringComparison.OrdinalIgnoreCase) || 
                $"fid_{f.Id}".Equals(session.MergeKeyFid, StringComparison.OrdinalIgnoreCase) ||
                $"fid_{f.Fid}".Equals(session.MergeKeyFid, StringComparison.OrdinalIgnoreCase));

            if (mergeField == null || !mergeField.Fid.HasValue)
                throw new InvalidOperationException($"Failed to resolve merge key field: '{session.MergeKeyFid}'");

            int inserted = 0;
            int updated = 0;
            var addedChanges = new List<PipelineRecordChange>();
            var modifiedChanges = new List<PipelineRecordChange>();

            await uow.BeginAsync(ct);
            try
            {
                var cachedOutput = await idempotencyRepo.GetByExecutionKeyAsync(messageGuid, step.PublicId, executionPathHash, uow.Transaction, ct);
                if (!string.IsNullOrEmpty(cachedOutput))
                {
                    await uow.CommitAsync(ct);
                    sessions.Remove(parentRefId);
                    return cachedOutput;
                }

                var addedRecords = new List<(Guid PublicId, Dictionary<long, object?> Row, Dictionary<long, object?> ChangeValues)>();

                foreach (var row in session.Rows)
                {
                    if (!row.TryGetValue(mergeField.Fid.Value, out var mergeVal) || mergeVal == null)
                    {
                        var recordPublicId = await recordRepo.CreateAsync(table, fields, row, uow.Transaction, ct);

                        var changeValues = new Dictionary<long, object?>();
                        foreach (var f in fields)
                        {
                            if (f.Fid.HasValue && row.TryGetValue(f.Fid.Value, out var val))
                                changeValues[f.Id] = val;
                        }

                        addedRecords.Add((recordPublicId, row, changeValues));
                        inserted++;
                        continue;
                    }

                    var filterCondition = new FilterCondition
                    {
                        FieldId = mergeField.Id,
                        Operator = "eq",
                        Value = mergeVal.ToString()
                    };
                    var filterTree = new FilterGroup
                    {
                        Logic = "and",
                        Nodes = new List<FilterNode> { new FilterNode { Condition = filterCondition } }
                    };

                    var existingRows = await recordRepo.ListAsync(table, fields, page: 1, pageSize: 1, filterTree: filterTree, ct: ct);
                    if (existingRows != null && existingRows.Any())
                    {
                        var existingRow = existingRows.First();
                        Guid? recordPublicId = null;
                        if (existingRow.TryGetValue("publicId", out var pubIdObj) && pubIdObj is Guid)
                        {
                            recordPublicId = (Guid)pubIdObj;
                        }
                        else if (existingRow.TryGetValue("id", out var idObj) && idObj is Guid)
                        {
                            recordPublicId = (Guid)idObj;
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
                                    var colKey = PowerBase.Domain.Constants.PhysicalNaming.GetPhysicalColumnName(f);
                                    var oldVal = existingRow.TryGetValue(colKey, out var ov) ? ov : null;
                                    var newVal = row.TryGetValue(f.Fid.Value, out var nv) ? nv : null;
                                    beforeValues[f.Id] = oldVal;
                                    afterValues[f.Id] = newVal;

                                    if (oldVal?.ToString() != newVal?.ToString())
                                    {
                                        changedFieldIds.Add(f.Id);
                                    }
                                }
                            }

                            await recordWriteService.ApplyAsync(
                                table, fields, recordPublicId.Value, row, AuditActions.Updated, "Record upserted via Pipeline bulk commit action step", ct, uow.Transaction, suppressInterception: true);

                            modifiedChanges.Add(new PipelineRecordChange(
                                recordPublicId.Value,
                                beforeValues,
                                afterValues,
                                changedFieldIds,
                                PipelineRecordEventType.Modified
                            ));
                            updated++;
                        }
                        else
                        {
                            var createdId = await recordRepo.CreateAsync(table, fields, row, uow.Transaction, ct);

                            var changeValues = new Dictionary<long, object?>();
                            foreach (var f in fields)
                            {
                                if (f.Fid.HasValue && row.TryGetValue(f.Fid.Value, out var val))
                                    changeValues[f.Id] = val;
                            }

                            addedRecords.Add((createdId, row, changeValues));
                            inserted++;
                        }
                    }
                    else
                    {
                        var createdId = await recordRepo.CreateAsync(table, fields, row, uow.Transaction, ct);

                        var changeValues = new Dictionary<long, object?>();
                        foreach (var f in fields)
                        {
                            if (f.Fid.HasValue && row.TryGetValue(f.Fid.Value, out var val))
                                changeValues[f.Id] = val;
                        }

                        addedRecords.Add((createdId, row, changeValues));
                        inserted++;
                    }
                }

                if (addedRecords.Any())
                {
                    var publicIds = addedRecords.Select(r => r.PublicId).ToList();
                    var publicIdToIdMap = await recordRepo.GetRecordIdsByPublicIdsAsync(table, publicIds, uow.Transaction, ct);

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
                if (addedChanges.Any())
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
                if (modifiedChanges.Any())
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

                var outputJson = JsonSerializer.Serialize(new { Committed = true, InsertedCount = inserted, UpdatedCount = updated });

                await idempotencyRepo.InsertAsync(new PipelineStepIdempotencyLog
                {
                    MessageId = messageGuid,
                    StepPublicId = step.PublicId,
                    ExecutionPathHash = executionPathHash,
                    ExecutionPath = executionPath,
                    OutputJson = outputJson
                }, uow.Transaction, ct);

                await uow.CommitAsync(ct);
                sessions.Remove(parentRefId);
                return outputJson;
            }
            catch (Exception ex) when (IsUniqueConstraintViolation(ex))
            {
                await uow.RollbackAsync(ct);
                var winningOutput = await idempotencyRepo.GetByExecutionKeyAsync(messageGuid, step.PublicId, executionPathHash, null, ct);
                if (winningOutput != null) return winningOutput;
                throw;
            }
            catch
            {
                await uow.RollbackAsync(ct);
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

            var resolvedUrl = EvaluateTokens(fileUrl, payloadJson);
            var resolvedFileName = EvaluateTokens(config.FileName, payloadJson);

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
                                    await uow.RollbackAsync(ct);
                                    var winningOutput = await idempotencyRepo.GetByExecutionKeyAsync(messageGuid, step.PublicId, executionPathHash, null, ct);
                                    if (winningOutput != null) return winningOutput;
                                    throw;
                                }
                                catch
                                {
                                    await uow.RollbackAsync(ct);
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
        else if (step.Type == "trigger" && subtype == "new-event")
        {
            var triggerInfo = new Dictionary<string, object?>();
            triggerInfo["Status"] = "Fired";
            triggerInfo["TriggerType"] = "On New Event";

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
            _logger.LogInformation("Step type '{Type}' / subtype '{Subtype}' is not handled inside execution engine. Skipping executing step.", step.Type, step.Subtype);
            return JsonSerializer.Serialize(new { Status = "Skipped", Type = step.Type, Subtype = step.Subtype });
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

        if (sourceVal is JsonElement jsonEl)
        {
            if (jsonEl.ValueKind == JsonValueKind.Array)
            {
                return jsonEl.EnumerateArray().Cast<object>();
            }
            if (jsonEl.ValueKind == JsonValueKind.Object)
            {
                if (jsonEl.TryGetProperty("records", out var recordsProp) && recordsProp.ValueKind == JsonValueKind.Array)
                {
                    return recordsProp.EnumerateArray().Cast<object>();
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

    private bool EvaluateConditionGroup(ConditionRuleGroup group, string payloadJson)
    {
        if (group.Rules == null || !group.Rules.Any()) return true;

        bool isAnd = group.LogicalOp.Equals("AND", StringComparison.OrdinalIgnoreCase);

        foreach (var rule in group.Rules)
        {
            bool ruleResult = false;
            if (rule.Type == "rule")
            {
                var left = EvaluateTokens(rule.Left, payloadJson);
                var right = EvaluateTokens(rule.Right, payloadJson);
                ruleResult = EvaluateConditionOperator(left, rule.Op ?? "equals", right);
            }
            else if (rule.Type == "nested" && rule.Groups != null)
            {
                ruleResult = rule.Groups.All(g => EvaluateConditionGroup(g, payloadJson));
            }

            if (isAnd && !ruleResult) return false;
            if (!isAnd && ruleResult) return true;
        }

        return isAnd;
    }

    private string EvaluateTokens(string? input, string payloadJson)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        if (string.IsNullOrEmpty(payloadJson)) return input;

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

            return result;
        }
        catch
        {
            return input;
        }
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
                return el.TryGetProperty(member, out _);
            }
            return false;
        }

        public bool TryGetValue(Scriban.TemplateContext context, Scriban.Parsing.SourceSpan span, object target, string member, out object value)
        {
            value = null!;
            if (target is JsonElement el && el.ValueKind == JsonValueKind.Object && el.TryGetProperty(member, out var prop))
            {
                value = ConvertJsonElement(prop)!;
                return true;
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

    private object? ParseValueType(string valueStr, string typeCode)
    {
        if (string.IsNullOrWhiteSpace(valueStr)) return null;

        var normalizedCode = typeCode.ToUpperInvariant();

        if (normalizedCode == "CHECKBOX" || normalizedCode == "BOOLEAN")
        {
            if (bool.TryParse(valueStr, out var bVal)) return bVal;
            if (valueStr == "1") return true;
            if (valueStr == "0") return false;
            return false;
        }
        if (new[] { "NUMERIC", "CURRENCY", "PERCENT", "INTEGER", "FLOAT", "NUMBER" }.Contains(normalizedCode))
        {
            if (decimal.TryParse(valueStr, out var dVal)) return dVal;
            return null;
        }
        if (new[] { "DATE", "DATE_TIME", "TIME_OF_DAY", "TIMESTAMP" }.Contains(normalizedCode))
        {
            if (DateTime.TryParse(valueStr, out var dtVal)) return dtVal;
            return null;
        }

        return valueStr; // Default to string
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
        public string? TableLabel { get; set; }
        public string? MergeKeyFid { get; set; }
    }

    private class AddBulkUpsertRowConfig
    {
        public string? ParentUpsertStepRefId { get; set; }
        public List<FieldMapping>? FieldMappings { get; set; }
    }

    private class CommitBulkUpsertConfig
    {
        public string? ParentUpsertStepRefId { get; set; }
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
        public List<Dictionary<long, object?>> Rows { get; set; } = new();
    }

    private class FieldMapping
    {
        public string? Field { get; set; }
        [System.Text.Json.Serialization.JsonConverter(typeof(StringOrPrimitiveJsonConverter))]
        public string? Value { get; set; }
    }

    private class SearchRecordsStepConfig
    {
        public string? TableId { get; set; }
        public string? FilterField { get; set; }
        [System.Text.Json.Serialization.JsonConverter(typeof(StringOrPrimitiveJsonConverter))]
        public string? FilterValue { get; set; }
        public int? MaxResults { get; set; }
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

