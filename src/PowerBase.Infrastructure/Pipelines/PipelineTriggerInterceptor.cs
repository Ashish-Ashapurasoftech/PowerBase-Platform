using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Enums;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Pipelines;

public class PipelineTriggerInterceptor : IPipelineTriggerInterceptor
{
    private readonly IPipelineRepository _pipelineRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IQueryContext _queryContext;
    private readonly ITenantUnitOfWork _uow;
    private readonly IControlConnectionFactory _controlConnFactory;
    private readonly IMainPipelineQueueRepository _mainQueueRepo;
    private readonly ILogger<PipelineTriggerInterceptor> _logger;

    public PipelineTriggerInterceptor(
        IPipelineRepository pipelineRepo,
        IRecordRepository recordRepo,
        IQueryContext queryContext,
        ITenantUnitOfWork _uow,
        IControlConnectionFactory controlConnFactory,
        IMainPipelineQueueRepository mainQueueRepo,
        ILogger<PipelineTriggerInterceptor> logger)
    {
        _pipelineRepo = pipelineRepo;
        _recordRepo = recordRepo;
        _queryContext = queryContext;
        this._uow = _uow;
        _controlConnFactory = controlConnFactory;
        _mainQueueRepo = mainQueueRepo;
        _logger = logger;
    }

    [Obsolete("Use the constructor with controlConnFactory and mainQueueRepo instead.")]
    public PipelineTriggerInterceptor(
        IPipelineRepository pipelineRepo,
        IRecordRepository recordRepo,
        IQueryContext queryContext,
        ITenantUnitOfWork _uow,
        ILogger<PipelineTriggerInterceptor> logger)
        : this(pipelineRepo, recordRepo, queryContext, _uow, null!, null!, logger)
    {
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
            // 2. Fetch matching active trigger subscriptions from the Control DB (with repository fallback for unit tests)
            IReadOnlyList<TriggerSubscription> subscriptions;
            if (_controlConnFactory == null)
            {
                IReadOnlyList<Pipeline> activePipelines;
                using (var suppressScope = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeOption.Suppress, System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
                {
                    activePipelines = await _pipelineRepo.ListAllActiveAsync(ct);
                }

                var mockSubs = new List<TriggerSubscription>();
                foreach (var pipeline in activePipelines)
                {
                    IReadOnlyList<PipelineStep> steps;
                    using (var suppressScope = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeOption.Suppress, System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
                    {
                        steps = await _pipelineRepo.GetStepsByPipelineIdAsync(pipeline.Id, ct);
                    }
                    var triggerStep = steps.FirstOrDefault(s => !s.IsDeleted && s.Type == "trigger" && s.Subtype == "new-event");
                    if (triggerStep == null || string.IsNullOrEmpty(triggerStep.ConfigJson)) continue;

                    NewEventStepConfig config;
                    try
                    {
                        config = JsonSerializer.Deserialize<NewEventStepConfig>(triggerStep.ConfigJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                            ?? throw new InvalidOperationException();
                    }
                    catch { continue; }

                    if (string.IsNullOrEmpty(config.TablePublicId) || !Guid.TryParse(config.TablePublicId, out var configTableGuid) || configTableGuid != table.PublicId)
                    {
                        continue;
                    }

                    mockSubs.Add(new TriggerSubscription
                    {
                        OwnerTenantId = _queryContext.TenantId,
                        OwnerPipelineId = pipeline.Id,
                        PipelinePublicId = pipeline.PublicId,
                        TriggerStepPublicId = triggerStep.PublicId,
                        TriggerStepRefId = triggerStep.RefId,
                        TargetTenantId = _queryContext.TenantId,
                        TargetAppPublicId = Guid.TryParse(config.AppPublicId, out var appGuid) ? appGuid : Guid.Empty,
                        TargetTablePublicId = configTableGuid,
                        TargetConnectionPublicId = Guid.TryParse(config.ConnectionPublicId, out var connGuid) ? connGuid : Guid.Empty,
                        TriggerOnAdded = config.TriggerOnAdded,
                        TriggerOnModified = config.TriggerOnModified,
                        TriggerOnDeleted = config.TriggerOnDeleted,
                        TriggerOnAnyField = config.TriggerOnAnyField,
                        TriggerFieldsJson = config.TriggerFields != null ? JsonSerializer.Serialize(config.TriggerFields) : null,
                        FiltersJson = config.Filters != null ? JsonSerializer.Serialize(config.Filters) : null,
                        FilterGroupsJson = config.FilterGroups != null ? JsonSerializer.Serialize(config.FilterGroups) : null,
                        LimitRecords = config.LimitRecords,
                        MaxRecords = config.MaxRecords
                    });
                }
                subscriptions = mockSubs;
            }
            else
            {
                using (var suppressScope = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeOption.Suppress, System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
                {
                    await using var controlConn = _controlConnFactory.Create();
                    await controlConn.OpenAsync(ct);
                    const string sql = """
                        SELECT * FROM meta.PipelineTriggerSubscription 
                        WHERE TargetTenantId = @TargetTenantId 
                          AND TargetTablePublicId = @TargetTablePublicId 
                          AND IsActive = 1
                        """;
                    var results = await controlConn.QueryAsync<TriggerSubscription>(
                        new CommandDefinition(sql, new { TargetTenantId = _queryContext.TenantId, TargetTablePublicId = table.PublicId }, cancellationToken: ct));
                    subscriptions = results.ToList();
                }
            }
            if (subscriptions.Count == 0) return;

            foreach (var sub in subscriptions)
            {
                // Prevent cyclic dependency loops
                if (currentChain.Contains(sub.OwnerPipelineId))
                {
                    _logger.LogWarning("Cyclic dependency detected: Pipeline {PipelineId} already executed in the call chain. Skipping execution trigger.", sub.OwnerPipelineId);
                    continue;
                }

                // Enforce bulk maxRecords limits using total count N before event/field filtering
                int totalChangedRecordCount = recordChanges.Count;
                if (sub.LimitRecords && totalChangedRecordCount > (sub.MaxRecords ?? 0))
                {
                    _logger.LogInformation("Pipeline {PipelineId}: Total changed record count {Count} exceeds MaxRecords limit {Max}. Skipping trigger runs.", sub.OwnerPipelineId, totalChangedRecordCount, sub.MaxRecords);
                    continue;
                }

                // Parse trigger config fields directly from subscription record
                var triggerFields = !string.IsNullOrEmpty(sub.TriggerFieldsJson) 
                    ? JsonSerializer.Deserialize<List<string>>(sub.TriggerFieldsJson) 
                    : null;
                var filters = !string.IsNullOrEmpty(sub.FiltersJson) 
                    ? JsonSerializer.Deserialize<List<PowerBase.Application.Pipelines.TriggerFilterRule>>(sub.FiltersJson) 
                    : null;
                var filterGroups = !string.IsNullOrEmpty(sub.FilterGroupsJson) 
                    ? JsonSerializer.Deserialize<List<PowerBase.Application.Pipelines.TriggerFilterGroup>>(sub.FilterGroupsJson) 
                    : null;

                // Group changes that match trigger event filters
                var matchingChanges = new List<PipelineRecordChange>();
                foreach (var change in recordChanges)
                {
                    if (PipelineEventMapper.IsEventEnabled(change.EventType, sub.TriggerOnAdded, sub.TriggerOnModified, sub.TriggerOnDeleted))
                    {
                        bool isCandidate = false;
                        if (change.EventType == PipelineRecordEventType.Modified)
                        {
                            if (sub.TriggerOnAnyField)
                            {
                                isCandidate = true;
                            }
                            else if (triggerFields != null && triggerFields.Any())
                            {
                                var triggerFids = triggerFields.Select(f => ParseFid(f)).Where(x => x.HasValue).Select(x => x!.Value).ToList();
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
                            var nonBlankGroups = filterGroups?
                                .Where(g => !PowerBase.Application.Pipelines.PipelineFilterEvaluator.IsGroupCompletelyBlank(g))
                                .ToList();

                            var nonBlankRules = filters?
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

                // Process matching changes
                bool isSameTenant = sub.OwnerTenantId == _queryContext.TenantId;

                foreach (var change in matchingChanges)
                {
                    var msgId = Guid.NewGuid();
                    var payloadDict = new Dictionary<string, object?>();

                    payloadDict["PayloadVersion"] = "1.0";
                    payloadDict["MessageId"] = msgId.ToString();
                    payloadDict["BatchId"] = batchId.ToString();
                    payloadDict["PipelineId"] = sub.OwnerPipelineId;
                    payloadDict["TriggerStepId"] = 0;
                    payloadDict["TriggerStepRefId"] = sub.TriggerStepRefId;
                    payloadDict["ConnectionPublicId"] = isSameTenant ? null : sub.TargetConnectionPublicId.ToString();
                    payloadDict["AppPublicId"] = sub.TargetAppPublicId.ToString();
                    payloadDict["TablePublicId"] = sub.TargetTablePublicId.ToString();
                    payloadDict["RecordPublicId"] = change.RecordPublicId.ToString();
                    payloadDict["EventType"] = PipelineEventMapper.MapToString(change.EventType);
                    payloadDict["BatchChangedRecordCount"] = totalChangedRecordCount;

                    var changedFieldFids = fields
                        .Where(f => change.ChangedFieldIds.Contains(f.Id) && f.Fid.HasValue)
                        .Select(f => $"fid_{f.Fid!.Value}")
                        .ToList();

                    payloadDict["ChangedFieldFids"] = changedFieldFids;

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

                    var nextChain = new List<long>(currentChain) { sub.OwnerPipelineId };
                    payloadDict["PipelineChain"] = nextChain;
                    payloadDict["TriggeredBy"] = actorUserId;
                    payloadDict["EventTimestamp"] = DateTime.UtcNow.ToString("o");

                    if (isSameTenant)
                    {
                        var outboxItem = new PipelineOutboxItem
                        {
                            PipelineId = sub.OwnerPipelineId,
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
                    else
                    {
                        var queueJob = new PipelineQueue
                        {
                            MessageId = msgId,
                            TenantId = sub.OwnerTenantId,
                            TenantPublicId = Guid.Empty,
                            PipelineId = sub.OwnerPipelineId,
                            PipelinePublicId = sub.PipelinePublicId,
                            QueueSource = "Event",
                            TriggerStepId = 0,
                            TriggerStepRefId = sub.TriggerStepRefId,
                            TriggerEvent = "new-event",
                            TriggerPayloadJson = JsonSerializer.Serialize(payloadDict),
                            PayloadHash = PowerBase.Infrastructure.Pipelines.PayloadHashHelper.ComputeHash(JsonSerializer.Serialize(payloadDict)),
                            TriggeredBy = actorUserId,
                            TriggerTablePublicId = table.PublicId,
                            CorrelationId = correlationId,
                            Depth = currentDepth,
                            PipelineChain = JsonSerializer.Serialize(nextChain),
                            BatchId = batchId,
                            PayloadVersion = "1.0",
                            EventTimestamp = DateTime.UtcNow,
                            Status = "Pending"
                        };

                        if (_uow is PowerBase.Infrastructure.UOW.TriggerPublishingTenantUnitOfWork publishUow)
                        {
                            publishUow.RegisterPostCommitAction(async () =>
                            {
                                try
                                {
                                    await _mainQueueRepo.EnqueueAsync(queueJob, null, ct);
                                    DatabasePipelineQueueWakeNotifier.Wake();
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "Failed to enqueue cross-tenant trigger from post-commit action for Pipeline {PipelineId}", sub.OwnerPipelineId);
                                }
                            });
                        }
                        else
                        {
                            await _mainQueueRepo.EnqueueAsync(queueJob, null, ct);
                            DatabasePipelineQueueWakeNotifier.Wake();
                        }
                    }
                }

                if (isSameTenant)
                {
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

    private class TriggerSubscription
    {
        public Guid PublicId { get; set; }
        public long OwnerTenantId { get; set; }
        public long OwnerPipelineId { get; set; }
        public Guid PipelinePublicId { get; set; }
        public Guid TriggerStepPublicId { get; set; }
        public string TriggerStepRefId { get; set; } = string.Empty;
        public long TargetTenantId { get; set; }
        public Guid TargetAppPublicId { get; set; }
        public Guid TargetTablePublicId { get; set; }
        public Guid TargetConnectionPublicId { get; set; }
        public bool TriggerOnAdded { get; set; }
        public bool TriggerOnModified { get; set; }
        public bool TriggerOnDeleted { get; set; }
        public bool TriggerOnAnyField { get; set; }
        public string? TriggerFieldsJson { get; set; }
        public string? FiltersJson { get; set; }
        public string? FilterGroupsJson { get; set; }
        public bool LimitRecords { get; set; }
        public int? MaxRecords { get; set; }
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
