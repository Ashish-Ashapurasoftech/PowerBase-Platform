using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Enums;

namespace PowerBase.Infrastructure.Pipelines;

public class PipelineTriggerInterceptor : IPipelineTriggerInterceptor
{
    private readonly IPipelineRepository _pipelineRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IQueryContext _queryContext;
    private readonly ITenantUnitOfWork _uow;
    private readonly ILogger<PipelineTriggerInterceptor> _logger;

    public PipelineTriggerInterceptor(
        IPipelineRepository pipelineRepo,
        IRecordRepository recordRepo,
        IQueryContext queryContext,
        ITenantUnitOfWork _uow,
        ILogger<PipelineTriggerInterceptor> logger)
    {
        _pipelineRepo = pipelineRepo;
        _recordRepo = recordRepo;
        _queryContext = queryContext;
        this._uow = _uow;
        _logger = logger;
    }

    public async Task InterceptAsync(
        AppTable table,
        IReadOnlyList<AppField> fields,
        Guid recordPublicId,
        IReadOnlyDictionary<long, object?> fieldValues,
        string triggerEvent,
        CancellationToken ct = default,
        IReadOnlyDictionary<long, object?>? beforeValues = null,
        IReadOnlyList<long>? changedFieldIds = null)
    {
        var mappedEvent = PipelineEventMapper.Map(triggerEvent);
        if (mappedEvent == null) return;

        var finalChangedIds = changedFieldIds?.ToList() ?? 
            (triggerEvent == "record-updated" ? fieldValues.Keys.ToList() : new List<long>());

        var finalBeforeValues = beforeValues ?? 
            (mappedEvent.Value == PipelineRecordEventType.Deleted ? fieldValues : new Dictionary<long, object?>());

        var finalAfterValues = mappedEvent.Value != PipelineRecordEventType.Deleted ? fieldValues : new Dictionary<long, object?>();

        var change = new PipelineRecordChange(
            recordPublicId,
            finalBeforeValues,
            finalAfterValues,
            finalChangedIds,
            mappedEvent.Value
        );

        await InterceptBulkAsync(table, fields, new[] { change }, Guid.NewGuid(), Guid.NewGuid(), _queryContext.UserId, ct);
    }

    public async Task InterceptBulkAsync(
        AppTable table,
        IReadOnlyList<AppField> fields,
        IReadOnlyList<PipelineRecordChange> recordChanges,
        Guid batchId,
        Guid correlationId,
        long? triggeredBy,
        CancellationToken ct = default)
    {
        if (recordChanges == null || recordChanges.Count == 0) return;

        var actorUserId = triggeredBy.GetValueOrDefault() > 0 
            ? triggeredBy.Value 
            : (_queryContext.UserId > 0 ? _queryContext.UserId : 0);

        // Validation Checks
        if (batchId == Guid.Empty || correlationId == Guid.Empty)
        {
            throw new PowerBase.Domain.Exceptions.ValidationException(new Dictionary<string, string[]>
            {
                ["Ids"] = new[] { "BatchId and CorrelationId must be valid non-empty Guids." }
            });
        }

        var firstEvent = recordChanges[0].EventType;
        var uniqueIds = new HashSet<Guid>();
        foreach (var rc in recordChanges)
        {
            if (rc.EventType != firstEvent)
            {
                throw new PowerBase.Domain.Exceptions.ValidationException(new Dictionary<string, string[]>
                {
                    ["EventType"] = new[] { "All records in a batch must represent the same event type." }
                });
            }
            if (rc.RecordPublicId == Guid.Empty)
            {
                throw new PowerBase.Domain.Exceptions.ValidationException(new Dictionary<string, string[]>
                {
                    ["RecordPublicId"] = new[] { "RecordPublicId cannot be empty." }
                });
            }
            if (!uniqueIds.Add(rc.RecordPublicId))
            {
                throw new PowerBase.Domain.Exceptions.ValidationException(new Dictionary<string, string[]>
                {
                    ["RecordPublicId"] = new[] { $"Duplicate RecordPublicId '{rc.RecordPublicId}' in the batch." }
                });
            }
        }

        // Strict transaction check
        if (_uow.Transaction == null)
        {
            throw new InvalidOperationException("An active transaction is required to write to the pipeline outbox. Ensure the mutation is wrapped inside an active unit of work transaction.");
        }

        // 1. Loop prevention / recursion depth check
        int currentDepth = 1;
        var currentChain = new List<long>();

        if (_queryContext.IsPipelineExecution)
        {
            currentDepth = _queryContext.PipelineDepth + 1;

            if (!string.IsNullOrEmpty(_queryContext.PipelineChainJson))
            {
                try
                {
                    currentChain = JsonSerializer.Deserialize<List<long>>(_queryContext.PipelineChainJson) ?? new List<long>();
                }
                catch
                {
                    // Fallback
                }
            }

            if (currentDepth > 10)
            {
                _logger.LogWarning("Recursion check: Depth threshold exceeded ({Depth}). Skipping outbox queue.", currentDepth);
                return;
            }
        }

        try
        {
            // 2. Fetch all active pipelines
            IReadOnlyList<Pipeline> activePipelines;
            using (var suppressScope = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeOption.Suppress, System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
            {
                activePipelines = await _pipelineRepo.ListAllActiveAsync(ct);
            }
            if (activePipelines.Count == 0) return;

            foreach (var pipeline in activePipelines)
            {
                // Prevent cyclic dependency loops
                if (currentChain.Contains(pipeline.Id))
                {
                    _logger.LogWarning("Cyclic dependency detected: Pipeline {PipelineId} already executed in the call chain. Skipping execution trigger.", pipeline.Id);
                    continue;
                }

                IReadOnlyList<PipelineStep> steps;
                using (var suppressScope = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeOption.Suppress, System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
                {
                    steps = await _pipelineRepo.GetStepsByPipelineIdAsync(pipeline.Id, ct);
                }
                var triggerStep = steps.FirstOrDefault(s => !s.IsDeleted && s.Type == "trigger" && s.Subtype == "new-event");
                if (triggerStep == null) continue;

                if (string.IsNullOrEmpty(triggerStep.ConfigJson)) continue;

                NewEventStepConfig config;
                try
                {
                    config = JsonSerializer.Deserialize<NewEventStepConfig>(triggerStep.ConfigJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                        ?? throw new InvalidOperationException();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to parse trigger config for Pipeline: {PipelineId}", pipeline.Id);
                    continue;
                }

                // Check table matches
                if (string.IsNullOrEmpty(config.TablePublicId) || !Guid.TryParse(config.TablePublicId, out var configTableGuid) || configTableGuid != table.PublicId)
                {
                    continue;
                }

                // Enforce bulk maxRecords limits using total count N before event/field filtering
                int totalChangedRecordCount = recordChanges.Count;
                if (config.LimitRecords && totalChangedRecordCount > (config.MaxRecords ?? 0))
                {
                    _logger.LogInformation("Pipeline {PipelineId}: Total changed record count {Count} exceeds MaxRecords limit {Max}. Skipping trigger runs.", pipeline.Id, totalChangedRecordCount, config.MaxRecords);
                    continue;
                }

                // Group changes that match trigger event filters
                var matchingChanges = new List<PipelineRecordChange>();
                foreach (var change in recordChanges)
                {
                    if (PipelineEventMapper.IsEventEnabled(change.EventType, config.TriggerOnAdded, config.TriggerOnModified, config.TriggerOnDeleted))
                    {
                        bool isCandidate = false;
                        if (change.EventType == PipelineRecordEventType.Modified)
                        {
                            if (config.TriggerOnAnyField)
                            {
                                isCandidate = true;
                            }
                            else if (config.TriggerFields != null && config.TriggerFields.Any())
                            {
                                var triggerFids = config.TriggerFields.Select(f => ParseFid(f)).Where(x => x.HasValue).Select(x => x!.Value).ToList();
                                var changedFids = fields.Where(f => change.ChangedFieldIds.Contains(f.Id) && f.Fid.HasValue).Select(f => f.Fid!.Value).ToList();
                                if (triggerFids.Intersect(changedFids).Any())
                                {
                                    isCandidate = true;
                                }
                            }
                        }
                        else
                        {
                            isCandidate = true;
                        }

                        if (isCandidate)
                        {
                            var valuesSource = change.EventType == PipelineRecordEventType.Deleted ? change.BeforeValues : change.AfterValues;
                            bool filtersMatch = true;
                            var nonBlankGroups = config.FilterGroups?
                                .Where(g => !PowerBase.Application.Pipelines.PipelineFilterEvaluator.IsGroupCompletelyBlank(g))
                                .ToList();

                            var nonBlankRules = config.Filters?
                                .Where(r => !PowerBase.Application.Pipelines.PipelineFilterEvaluator.IsRuleCompletelyBlank(r))
                                .ToList();

                            if (nonBlankGroups != null && nonBlankGroups.Any())
                            {
                                filtersMatch = nonBlankGroups.Any(g => PowerBase.Application.Pipelines.PipelineFilterEvaluator.EvaluateGroup(g, valuesSource, fields, _logger));
                            }
                            else if (nonBlankRules != null && nonBlankRules.Any())
                            {
                                var mockGroup = new PowerBase.Application.Pipelines.TriggerFilterGroup { LogicalOp = "AND", Rules = nonBlankRules };
                                filtersMatch = PowerBase.Application.Pipelines.PipelineFilterEvaluator.EvaluateGroup(mockGroup, valuesSource, fields, _logger);
                            }

                            if (filtersMatch)
                            {
                                matchingChanges.Add(change);
                            }
                        }
                    }
                }

                if (matchingChanges.Count == 0) continue;

                // Generate outbox rows inside the current transaction
                foreach (var change in matchingChanges)
                {
                    var msgId = Guid.NewGuid();
                    var payloadDict = new Dictionary<string, object?>();

                    payloadDict["PayloadVersion"] = "1.0";
                    payloadDict["MessageId"] = msgId.ToString();
                    payloadDict["BatchId"] = batchId.ToString();
                    payloadDict["PipelineId"] = pipeline.Id;
                    payloadDict["TriggerStepId"] = triggerStep.Id;
                    payloadDict["TriggerStepRefId"] = triggerStep.RefId;
                    payloadDict["ConnectionPublicId"] = config.ConnectionPublicId;
                    payloadDict["AppPublicId"] = config.AppPublicId;
                    payloadDict["TablePublicId"] = config.TablePublicId;
                    payloadDict["RecordPublicId"] = change.RecordPublicId.ToString();
                    payloadDict["EventType"] = PipelineEventMapper.MapToString(change.EventType);
                    payloadDict["BatchChangedRecordCount"] = totalChangedRecordCount;

                    // Field IDs to Stable FID strings conversion
                    var changedFieldFids = fields
                        .Where(f => change.ChangedFieldIds.Contains(f.Id) && f.Fid.HasValue)
                        .Select(f => $"fid_{f.Fid!.Value}")
                        .ToList();

                    payloadDict["ChangedFieldFids"] = changedFieldFids;

                    // Populate SelectedFieldValues
                    var valuesSource = change.EventType == PipelineRecordEventType.Deleted ? change.BeforeValues : change.AfterValues;
                    var selectedValues = new Dictionary<string, object?>();
                    foreach (var f in fields)
                    {
                        if (f.Fid.HasValue)
                        {
                            if (valuesSource.TryGetValue(f.Id, out var val))
                            {
                                selectedValues[$"fid_{f.Fid.Value}"] = val;
                            }
                            else if (valuesSource.TryGetValue(f.Fid.Value, out var valByFid))
                            {
                                selectedValues[$"fid_{f.Fid.Value}"] = valByFid;
                            }
                        }
                    }
                    payloadDict["SelectedFieldValues"] = selectedValues;

                    // Event-specific Old/New value serialization (Added/Modified/Deleted semantics)
                    if (change.EventType == PipelineRecordEventType.Added)
                    {
                        var newValues = new Dictionary<string, object?>();
                        foreach (var f in fields)
                        {
                            if (f.Fid.HasValue)
                            {
                                if (change.AfterValues.TryGetValue(f.Id, out var val))
                                    newValues[$"fid_{f.Fid.Value}"] = val;
                                else if (change.AfterValues.TryGetValue(f.Fid.Value, out var valByFid))
                                    newValues[$"fid_{f.Fid.Value}"] = valByFid;
                            }
                        }
                        payloadDict["NewValues"] = newValues;
                        payloadDict["OldValues"] = null;
                    }
                    else if (change.EventType == PipelineRecordEventType.Modified)
                    {
                        var oldValues = new Dictionary<string, object?>();
                        var newValues = new Dictionary<string, object?>();
                        foreach (var f in fields)
                        {
                            if (f.Fid.HasValue)
                            {
                                if (change.BeforeValues.TryGetValue(f.Id, out var oval))
                                    oldValues[$"fid_{f.Fid.Value}"] = oval;
                                else if (change.BeforeValues.TryGetValue(f.Fid.Value, out var ovalByFid))
                                    oldValues[$"fid_{f.Fid.Value}"] = ovalByFid;

                                if (change.AfterValues.TryGetValue(f.Id, out var nval))
                                    newValues[$"fid_{f.Fid.Value}"] = nval;
                                else if (change.AfterValues.TryGetValue(f.Fid.Value, out var nvalByFid))
                                    newValues[$"fid_{f.Fid.Value}"] = nvalByFid;
                            }
                        }
                        payloadDict["OldValues"] = oldValues;
                        payloadDict["NewValues"] = newValues;
                    }
                    else if (change.EventType == PipelineRecordEventType.Deleted)
                    {
                        var oldValues = new Dictionary<string, object?>();
                        foreach (var f in fields)
                        {
                            if (f.Fid.HasValue)
                            {
                                if (change.BeforeValues.TryGetValue(f.Id, out var oval))
                                    oldValues[$"fid_{f.Fid.Value}"] = oval;
                                else if (change.BeforeValues.TryGetValue(f.Fid.Value, out var ovalByFid))
                                    oldValues[$"fid_{f.Fid.Value}"] = ovalByFid;
                            }
                        }
                        payloadDict["OldValues"] = oldValues;
                        payloadDict["NewValues"] = null;
                    }

                    payloadDict["CorrelationId"] = correlationId.ToString();
                    payloadDict["Depth"] = currentDepth;

                    var nextChain = new List<long>(currentChain) { pipeline.Id };
                    payloadDict["PipelineChain"] = nextChain;
                    payloadDict["TriggeredBy"] = actorUserId;
                    payloadDict["EventTimestamp"] = DateTime.UtcNow.ToString("o");

                    var outboxItem = new PipelineOutboxItem
                    {
                        PipelineId = pipeline.Id,
                        TriggerEvent = "new-event",
                        TriggerPayloadJson = JsonSerializer.Serialize(payloadDict),
                        TriggeredBy = actorUserId,
                        TriggerTablePublicId = table.PublicId,
                        CorrelationId = correlationId,
                        Depth = currentDepth,
                        PipelineChain = JsonSerializer.Serialize(nextChain),
                        MessageId = msgId,
                        BatchId = batchId,
                        PayloadVersion = "1.0",
                        Published = 0
                    };

                    await _pipelineRepo.CreateOutboxItemAsync(outboxItem, _uow.Transaction, ct);
                }

                // If in transaction and post-commit action register is supported, trigger wake event on dispatcher
                if (_uow.Transaction != null && _uow is PowerBase.Infrastructure.UOW.TriggerPublishingTenantUnitOfWork publishUow)
                {
                    publishUow.RegisterPostCommitAction(async () =>
                    {
                        PipelineOutboxWakeNotifier.Wake();
                        await Task.CompletedTask;
                    });
                }
                else
                {
                    PipelineOutboxWakeNotifier.Wake();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during batch pipeline trigger interception.");
        }
    }

    private static int? ParseFid(string fidStr)
    {
        if (string.IsNullOrEmpty(fidStr)) return null;
        var s = fidStr.ToLower().Trim();
        if (s.StartsWith("fid_") && int.TryParse(s.Substring(4), out var result))
        {
            return result;
        }
        if (int.TryParse(s, out var directResult))
        {
            return directResult;
        }
        return null;
    }

    private class NewEventStepConfig
    {
        public string? ConnectionPublicId { get; set; }
        public string? AppPublicId { get; set; }
        public string? TablePublicId { get; set; }
        public bool TriggerOnAdded { get; set; }
        public bool TriggerOnModified { get; set; }
        public bool TriggerOnDeleted { get; set; }
        public bool TriggerOnAnyField { get; set; }
        public List<string>? TriggerFields { get; set; }
        public List<string>? SubsequentFields { get; set; }
        public bool LimitRecords { get; set; }
        public int? MaxRecords { get; set; }
        public List<PowerBase.Application.Pipelines.TriggerFilterRule>? Filters { get; set; }
        public List<PowerBase.Application.Pipelines.TriggerFilterGroup>? FilterGroups { get; set; }
    }
}

public static class PipelineOutboxWakeNotifier
{
    private static TaskCompletionSource<bool>? _wakeTcs;
    private static readonly object _lock = new();

    public static Task WaitForOutboxItemAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_wakeTcs == null || _wakeTcs.Task.IsCompleted)
            {
                _wakeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            return _wakeTcs.Task.WaitAsync(ct);
        }
    }

    public static void Wake()
    {
        lock (_lock)
        {
            _wakeTcs?.TrySetResult(true);
        }
    }
}
