using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Formulas;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Constants;
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
    private readonly PipelineExecutionOptions _options;
    private readonly ITenantConnectionResolver? _tenantResolver;
    private readonly IFormulaProjector? _formulaProjector;
    private readonly IRelationalProjector? _relationalProjector;

    public PipelineTriggerInterceptor(
        IPipelineRepository pipelineRepo,
        IRecordRepository recordRepo,
        IQueryContext queryContext,
        ITenantUnitOfWork _uow,
        IControlConnectionFactory controlConnFactory,
        IMainPipelineQueueRepository mainQueueRepo,
        ILogger<PipelineTriggerInterceptor> logger,
        IOptions<PipelineExecutionOptions>? options = null,
        ITenantConnectionResolver? tenantResolver = null,
        IFormulaProjector? formulaProjector = null,
        IRelationalProjector? relationalProjector = null)
    {
        _formulaProjector = formulaProjector;
        _relationalProjector = relationalProjector;
        _tenantResolver = tenantResolver;
        _pipelineRepo = pipelineRepo;
        _recordRepo = recordRepo;
        _queryContext = queryContext;
        this._uow = _uow;
        _controlConnFactory = controlConnFactory;
        _mainQueueRepo = mainQueueRepo;
        _logger = logger;
        _options = options?.Value ?? new PipelineExecutionOptions();
    }

    [Obsolete("Use the constructor with controlConnFactory and mainQueueRepo instead.")]
    public PipelineTriggerInterceptor(
        IPipelineRepository pipelineRepo,
        IRecordRepository recordRepo,
        IQueryContext queryContext,
        ITenantUnitOfWork _uow,
            ILogger<PipelineTriggerInterceptor> logger)
        : this(pipelineRepo, recordRepo, queryContext, _uow, null!, null!, logger, null)
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

        // Single-record writes report table FIDs; bulk changes use metadata IDs.
        // Convert at this boundary rather than guessing both namespaces when matching.
        var changedFids = changedFieldIds ?? fieldValues.Keys.ToList();
        var finalChangedIds = triggerEvent == "record-updated"
            ? fields.Where(f => f.Fid.HasValue && changedFids.Contains(f.Fid.Value)).Select(f => f.Id).Distinct().ToList()
            : new List<long>();

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

    /// <summary>Fids of the Reference fields at least one listening trigger filters on (by field name, fid_N
    /// token or advanced-query {N.…} id) — only those need their display text projected.</summary>
    private static IReadOnlyCollection<long> FilterReferenceFids(IReadOnlyList<TriggerSubscription> subscriptions, IReadOnlyList<AppField> fields)
    {
        var result = new List<long>();
        foreach (var field in fields.Where(f => f.Fid.HasValue && !f.IsDeleted && f.TypeCode == "Reference"))
        {
            var fid = field.Fid!.Value;
            var used = subscriptions.Any(s => new[] { s.FiltersJson, s.FilterGroupsJson, s.AdvancedQuery }.Any(text =>
                !string.IsNullOrEmpty(text) &&
                (text.Contains($"fid_{fid}", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains($"fid_{field.Id}", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains($"{{{fid}.", StringComparison.Ordinal) ||
                 text.Contains($"\"{field.Name}\"", StringComparison.OrdinalIgnoreCase))));
            if (used) result.Add(fid);
        }
        return result;
    }

    /// <summary>The values a trigger filter reads: the event values, with each Reference replaced by its
    /// display text (the user-visible value) and the stored Record ID# kept under the negated Fid so an
    /// exact-match condition on the id still matches.</summary>
    private static IReadOnlyDictionary<long, object?> FilterValues(PipelineRecordChange change, IReadOnlyDictionary<long, object?> values)
    {
        if (change.ReferenceLabels is not { Count: > 0 } labels) return values;
        var merged = new Dictionary<long, object?>(values);
        foreach (var (fid, label) in labels)
        {
            if (merged.TryGetValue(fid, out var stored) && stored is not null && stored is not string)
                merged[-fid] = stored;
            else if (merged.TryGetValue(fid, out var storedText) && storedText is string s && long.TryParse(s, out _))
                merged[-fid] = storedText;
            merged[fid] = label;
        }
        return merged;
    }

    /// <summary>Formula (compute-on-read) fields have no stored value, so the written values never
    /// contain them and an event filter / pipeline condition on one (e.g. CustomerNumber) would see
    /// blank. Re-reads the affected rows inside the open transaction (in chunks, to stay under SQL
    /// Server's parameter limit) and projects formula fields into each change's values — After for
    /// added/modified, Before for deleted (delete events fire before the row is removed). Every
    /// single and bulk write funnels through here. Best-effort: on any failure the changes are
    /// returned untouched.</summary>
    private async Task<IReadOnlyList<PipelineRecordChange>> WithComputedValuesAsync(
        AppTable table,
        IReadOnlyList<AppField> fields,
        IReadOnlyList<PipelineRecordChange> changes,
        CancellationToken ct,
        IReadOnlyCollection<long>? filterReferenceFids = null)
    {
        if (_formulaProjector == null || _uow.Transaction == null) return changes;
        var hasComputed = fields.Any(f => f.Fid.HasValue && !f.IsDeleted && PhysicalNaming.IsComputedTypeCode(f.TypeCode));
        // A Reference a trigger filter matches by display text needs its label projected too (it is
        // only used to evaluate the filter — the event values keep the stored Record ID#).
        var referenceFids = filterReferenceFids ?? Array.Empty<long>();
        if (!hasComputed && referenceFids.Count == 0) return changes;

        try
        {
            var result = new List<PipelineRecordChange>(changes.Count);
            const int chunkSize = 1000;
            foreach (var chunk in changes.Chunk(chunkSize))
            {
                var idMap = await _recordRepo.GetRecordIdsByPublicIdsAsync(
                    table, chunk.Select(c => c.RecordPublicId).ToList(), _uow.Transaction, ct);
                var rowsById = await _recordRepo.GetBulkUpsertRowsByIdsAsync(
                    table, fields, idMap.Values.ToList(), _uow.Transaction, ct);

                var present = chunk.Where(c => idMap.TryGetValue(c.RecordPublicId, out var id) && rowsById.ContainsKey(id)).ToList();
                var rows = present.Select(c => rowsById[idMap[c.RecordPublicId]]).ToList();
                // Read on other connections, these can wait behind the lock of the write being intercepted (see
                // PipelineComputedProjection.RelationshipBudget): give up after the budget instead of SQL's 30 s timeout.
                using var projectionBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                projectionBudget.CancelAfter(PowerBase.Application.Pipelines.PipelineComputedProjection.InTransactionBudget);
                var seed = _relationalProjector != null
                    ? await _relationalProjector.ProjectAsync(table, fields, rows, projectionBudget.Token)
                    : null;
                var computed = rows.Count > 0 ? _formulaProjector.Project(fields, rows, seed, table) : [];

                var computedByRecord = new Dictionary<Guid, IReadOnlyDictionary<long, object?>>();
                for (var i = 0; i < present.Count; i++) computedByRecord[present[i].RecordPublicId] = computed[i];

                foreach (var change in chunk)
                {
                    if (!computedByRecord.TryGetValue(change.RecordPublicId, out var values))
                    {
                        result.Add(change);
                        continue;
                    }
                    var labels = referenceFids
                        .Where(fid => values.TryGetValue(fid, out var label) && label is not null)
                        .ToDictionary(fid => fid, fid => values[fid]);
                    var withLabels = change with { ReferenceLabels = labels.Count > 0 ? labels : null };
                    if (!hasComputed)
                        result.Add(withLabels);
                    else if (change.EventType == PipelineRecordEventType.Deleted)
                        result.Add(withLabels with { BeforeValues = Merge(change.BeforeValues, values) });
                    else
                        result.Add(withLabels with { AfterValues = Merge(change.AfterValues, values) });
                }
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not project formula fields into the bulk pipeline events; continuing without them.");
            return changes;
        }

        static IReadOnlyDictionary<long, object?> Merge(IReadOnlyDictionary<long, object?> source, IReadOnlyDictionary<long, object?> computed)
        {
            var merged = new Dictionary<long, object?>(source);
            foreach (var kvp in computed) merged[kvp.Key] = kvp.Value;
            return merged;
        }
    }

    /// <summary>Writes bulk-event staging rows into another tenant's database, on its own connection.</summary>
    protected virtual async Task InsertStagingIntoTenantAsync(long tenantId, List<PipelineBulkEventRecord> records, CancellationToken ct)
    {
        if (records.Count == 0) return;
        var connectionString = await _tenantResolver!.ResolveAsync(tenantId, ct);
        using var suppressScope = new System.Transactions.TransactionScope(
            System.Transactions.TransactionScopeOption.Suppress, System.Transactions.TransactionScopeAsyncFlowOption.Enabled);
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(PowerBase.Infrastructure.Repositories.PipelineBulkEventSql.Insert, records, cancellationToken: ct));
        suppressScope.Complete();
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

        var uniqueIds = new HashSet<Guid>();
        var firstEventType = recordChanges[0].EventType;
        foreach (var rc in recordChanges)
        {
            if (rc.EventType != firstEventType)
            {
                throw new PowerBase.Domain.Exceptions.ValidationException(new Dictionary<string, string[]>
                {
                    ["EventType"] = new[] { "All records in a bulk operation batch must share the same EventType." }
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
                //_logger.LogWarning("Recursion check: Depth threshold exceeded ({Depth}). Skipping outbox queue.", currentDepth);
                //return;
                _logger.LogInformation("Recursion check: Depth threshold exceeded 10 ({Depth}). Continuing recursion indefinitely as configured.", currentDepth);
            }
        }

        try
        {
            // 2. Fetch matching active trigger subscriptions from the Control DB
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
                    var triggerStep = steps.FirstOrDefault(s => !s.IsDeleted && s.Type == "trigger" && (s.Subtype == "new-event" || s.Subtype == "new-bulk-event"));
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
                        IsSimpleFilter = config.IsSimpleFilter,
                        AdvancedQuery = config.AdvancedQuery,
                        LimitRecords = config.LimitRecords,
                        MaxRecords = config.MaxRecords,
                        TriggerSubtype = triggerStep.Subtype
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

            // Formula fields are compute-on-read; put their values in the events (see
            // WithComputedValuesAsync) now that we know at least one pipeline is listening.
            recordChanges = await WithComputedValuesAsync(table, fields, recordChanges, ct, FilterReferenceFids(subscriptions, fields));

            foreach (var sub in subscriptions)
            {
                // Prevent cyclic dependency loops
                if (currentChain.Contains(sub.OwnerPipelineId))
                {
                    _logger.LogInformation("Cyclic/recursive path detected: Pipeline {PipelineId} already executed in the call chain. Processing trigger recursion normally.", sub.OwnerPipelineId);
                    //continue;
                }

                // Thresholds select the source batch; filters select records within it.
                // Both boundaries are inclusive, and separate batches are never accumulated.
                if (sub.LimitRecords)
                {
                    var limit = sub.MaxRecords ?? 1;
                    var isBulk = sub.TriggerSubtype == "new-bulk-event";
                    var outsideThreshold = isBulk ? recordChanges.Count < limit : recordChanges.Count > limit;
                    if (outsideThreshold)
                    {
                        _logger.LogInformation("Pipeline {PipelineId}: Source batch size {Count} is outside {ThresholdType} threshold {Limit}. Skipping trigger generation.",
                            sub.OwnerPipelineId, recordChanges.Count, isBulk ? "minimum" : "maximum", limit);
                        continue;
                    }
                }

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
                            else if (!string.IsNullOrEmpty(sub.TriggerFieldsJson))
                            {
                                var triggerFields = JsonSerializer.Deserialize<List<string>>(sub.TriggerFieldsJson);
                                if (triggerFields != null && triggerFields.Any())
                                {
                                    var triggerFids = triggerFields.Select(f => ParseFid(f)).Where(x => x.HasValue).Select(x => x!.Value).ToList();
                                    var changedFids = fields.Where(f => f.Fid.HasValue && change.ChangedFieldIds.Contains(f.Id)).Select(f => f.Fid!.Value).ToList();
                                    if (triggerFids.Intersect(changedFids).Any())
                                    {
                                        isCandidate = true;
                                    }
                                }
                            }
                        }
                        else
                        {
                            isCandidate = true;
                        }

                        if (isCandidate)
                        {
                            var valuesSource = FilterValues(change, change.EventType == PipelineRecordEventType.Deleted ? change.BeforeValues : change.AfterValues);
                            bool filtersMatch = true;

                            if (!sub.IsSimpleFilter && !string.IsNullOrWhiteSpace(sub.AdvancedQuery))
                            {
                                try
                                {
                                    var parsedTree = PowerBase.Application.Pipelines.CopyRecordsDefinition.ParseQuery(sub.AdvancedQuery, fields);
                                    filtersMatch = PowerBase.Application.Pipelines.PipelineFilterEvaluator.EvaluateFilterGroup(parsedTree, valuesSource, fields, _logger);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "Pipeline {PipelineId}: On New Event Advanced Query failed to evaluate; skipping trigger match for this subscription.", sub.OwnerPipelineId);
                                    filtersMatch = false;
                                }
                            }
                            else
                            {
                                var filters = !string.IsNullOrEmpty(sub.FiltersJson)
                                    ? JsonSerializer.Deserialize<List<PowerBase.Application.Pipelines.TriggerFilterRule>>(sub.FiltersJson)
                                    : null;
                                var filterGroups = !string.IsNullOrEmpty(sub.FilterGroupsJson)
                                    ? JsonSerializer.Deserialize<List<PowerBase.Application.Pipelines.TriggerFilterGroup>>(sub.FilterGroupsJson)
                                    : null;

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
                            }

                            if (filtersMatch)
                            {
                                matchingChanges.Add(change);
                            }
                        }
                    }
                }

                if (matchingChanges.Count == 0) continue;

                bool isSameTenant = sub.OwnerTenantId == _queryContext.TenantId;
                var nextChain = new List<long>(currentChain) { sub.OwnerPipelineId };

                // BRANCH A: On New Bulk Event Execution
                if (sub.TriggerSubtype == "new-bulk-event")
                {
                    var bulkEventId = Guid.NewGuid();
                    var ordinal = 1;
                    var stagingRecords = new List<PowerBase.Domain.Entities.PipelineBulkEventRecord>();

                    foreach (var change in matchingChanges)
                    {
                        var beforeValuesJson = change.BeforeValues != null && change.BeforeValues.Any() 
                            ? JsonSerializer.Serialize(MapValues(change.BeforeValues, fields)) 
                            : null;
                        var afterValuesJson = change.AfterValues != null && change.AfterValues.Any() 
                            ? JsonSerializer.Serialize(MapValues(change.AfterValues, fields)) 
                            : null;
                        var changedFieldFids = fields
                            .Where(f => change.ChangedFieldIds.Contains(f.Id) && f.Fid.HasValue)
                            .Select(f => $"fid_{f.Fid!.Value}")
                            .ToList();
                        var changedFieldsJson = changedFieldFids.Any() ? JsonSerializer.Serialize(changedFieldFids) : null;

                        stagingRecords.Add(new PowerBase.Domain.Entities.PipelineBulkEventRecord
                        {
                            BulkEventId = bulkEventId,
                            Ordinal = ordinal++,
                            RecordPublicId = change.RecordPublicId,
                            EventType = PipelineEventMapper.MapToString(change.EventType),
                            BeforeValuesJson = beforeValuesJson,
                            AfterValuesJson = afterValuesJson,
                            ChangedFieldsJson = changedFieldsJson,
                            Processed = 0,
                            CreatedOn = DateTime.UtcNow
                        });
                    }

                    // The flow runs, and reads its staged records, in the owner tenant's database. For a
                    // cross-tenant trigger the staging therefore goes there (written after this
                    // transaction commits, just before the job is queued) instead of into this tenant.
                    var stageInOwnerTenant = !isSameTenant && _tenantResolver != null;
                    if (!stageInOwnerTenant)
                        await _pipelineRepo.InsertBulkEventRecordsAsync(stagingRecords, _uow.Transaction, ct);

                    // Map centralized queue payload
                    var payloadDict = new Dictionary<string, object?>();
                    payloadDict["PayloadVersion"] = "1.0";
                    payloadDict["MessageId"] = bulkEventId.ToString();
                    payloadDict["BatchId"] = batchId.ToString();
                    payloadDict["PipelineId"] = sub.OwnerPipelineId;
                    payloadDict["TriggerStepId"] = 0;
                    payloadDict["TriggerStepRefId"] = sub.TriggerStepRefId;
                    payloadDict["ConnectionPublicId"] = isSameTenant ? null : sub.TargetConnectionPublicId.ToString();
                    payloadDict["AppPublicId"] = sub.TargetAppPublicId.ToString();
                    payloadDict["TablePublicId"] = sub.TargetTablePublicId.ToString();
                    payloadDict["EventType"] = "bulk";
                    payloadDict["Count"] = matchingChanges.Count;
                    payloadDict["CorrelationId"] = correlationId.ToString();
                    payloadDict["Depth"] = currentDepth;
                    payloadDict["PipelineChain"] = nextChain;
                    payloadDict["TriggeredBy"] = actorUserId;
                    payloadDict["EventTimestamp"] = DateTime.UtcNow.ToString("o");

                    if (isSameTenant)
                    {
                        var outboxItem = new PipelineOutboxItem
                        {
                            PipelineId = sub.OwnerPipelineId,
                            TriggerEvent = "new-bulk-event",
                            TriggerPayloadJson = JsonSerializer.Serialize(payloadDict),
                            TriggeredBy = actorUserId,
                            TriggerTablePublicId = table.PublicId,
                            CorrelationId = correlationId,
                            Depth = currentDepth,
                            PipelineChain = JsonSerializer.Serialize(nextChain),
                            MessageId = bulkEventId,
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
                            MessageId = bulkEventId,
                            TenantId = sub.OwnerTenantId,
                            TenantPublicId = Guid.Empty,
                            PipelineId = sub.OwnerPipelineId,
                            PipelinePublicId = sub.PipelinePublicId,
                            QueueSource = "Event",
                            TriggerStepId = 0,
                            TriggerStepRefId = sub.TriggerStepRefId,
                            TriggerEvent = "new-bulk-event",
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
                            LockedBy = null,
                            LockedUntil = null,
                            AttemptCount = 0,
                            MaxAttempts = _options.DatabaseQueue.MaxAttempts,
                            Status = "Pending"
                        };

                        async Task StageAndEnqueueAsync()
                        {
                            if (stageInOwnerTenant)
                                await InsertStagingIntoTenantAsync(sub.OwnerTenantId, stagingRecords, ct);
                            await _mainQueueRepo.EnqueueAsync(queueJob, null, ct);
                            DatabasePipelineQueueWakeNotifier.Wake();
                        }

                        if (_uow is PowerBase.Infrastructure.UOW.TriggerPublishingTenantUnitOfWork publishUow)
                        {
                            publishUow.RegisterPostCommitAction(async () =>
                            {
                                try
                                {
                                    await StageAndEnqueueAsync();
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "Failed to enqueue cross-tenant trigger from post-commit action for Pipeline {PipelineId}", sub.OwnerPipelineId);
                                }
                            });
                        }
                        else
                        {
                            await StageAndEnqueueAsync();
                        }
                    }
                }
                // BRANCH B: On New Event Execution (Single record flow)
                else
                {
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
                        payloadDict["BatchChangedRecordCount"] = recordChanges.Count;

                        var changedFieldFids = fields
                            .Where(f => change.ChangedFieldIds.Contains(f.Id) && f.Fid.HasValue)
                            .Select(f => $"fid_{f.Fid!.Value}")
                            .ToList();

                        payloadDict["ChangedFieldFids"] = changedFieldFids;
                        payloadDict["SelectedFieldValues"] = MapValues(change.EventType == PipelineRecordEventType.Deleted ? change.BeforeValues : change.AfterValues, fields);

                        if (change.EventType == PipelineRecordEventType.Added)
                        {
                            payloadDict["NewValues"] = MapValues(change.AfterValues, fields);
                            payloadDict["OldValues"] = null;
                        }
                        else if (change.EventType == PipelineRecordEventType.Modified)
                        {
                            payloadDict["OldValues"] = MapValues(change.BeforeValues, fields);
                            payloadDict["NewValues"] = MapValues(change.AfterValues, fields);
                        }
                        else if (change.EventType == PipelineRecordEventType.Deleted)
                        {
                            payloadDict["OldValues"] = MapValues(change.BeforeValues, fields);
                            payloadDict["NewValues"] = null;
                        }

                        payloadDict["CorrelationId"] = correlationId.ToString();
                        payloadDict["Depth"] = currentDepth;

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
                                LockedBy = null,
                                LockedUntil = null,
                                AttemptCount = 0,
                                MaxAttempts = _options.DatabaseQueue.MaxAttempts,
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
            throw; // Do not commit the record mutation without its trigger events.
        }
    }

    private static Dictionary<string, object?> MapValues(IReadOnlyDictionary<long, object?> values, IReadOnlyList<AppField> fields)
    {
        var result = new Dictionary<string, object?>();
        if (values == null) return result;
        foreach (var f in fields)
        {
            if (f.Fid.HasValue)
            {
                if (values.TryGetValue(f.Id, out var val))
                    result[$"fid_{f.Fid.Value}"] = val;
                else if (values.TryGetValue(f.Fid.Value, out var valByFid))
                    result[$"fid_{f.Fid.Value}"] = valByFid;
            }
        }
        return result;
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
        [System.Text.Json.Serialization.JsonConverter(typeof(PowerBase.Application.Pipelines.RecordLimitJsonConverter))]
        public int? MaxRecords { get; set; }
        public List<PowerBase.Application.Pipelines.TriggerFilterRule>? Filters { get; set; }
        public List<PowerBase.Application.Pipelines.TriggerFilterGroup>? FilterGroups { get; set; }
        public bool IsSimpleFilter { get; set; } = true;
        public string? AdvancedQuery { get; set; }
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
        public bool IsSimpleFilter { get; set; } = true;
        public string? AdvancedQuery { get; set; }
        public bool LimitRecords { get; set; }
        public int? MaxRecords { get; set; }
        public string TriggerSubtype { get; set; } = "new-event";
    }
}

public static class PipelineOutboxWakeNotifier
{
    private static readonly PipelineWakeSignal Signal = new();

    public static Task WaitForOutboxItemAsync(CancellationToken ct) => Signal.WaitAsync(Timeout.InfiniteTimeSpan, ct);
    public static Task WaitForOutboxItemAsync(TimeSpan timeout, CancellationToken ct) => Signal.WaitAsync(timeout, ct);
    public static void Wake() => Signal.Wake();
}
