using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Enums;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.ValueObjects;
using PowerBase.Formula;

namespace PowerBase.Application.Records.Commands.MassUpdateRecords;

public class MassUpdateRecordsCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IRelationshipRepository _relRepo;
    private readonly IRolePermissionEnforcer _enforcer;
    private readonly IAuditRepository _auditRepo;
    private readonly IPipelineTriggerInterceptor _triggerInterceptor;
    private readonly ITenantUnitOfWork _uow;
    private readonly IQueryContext _queryContext;
    private readonly IAppRepository _appRepo;
    private readonly IMessagePublisher _messagePublisher;
    private readonly FormulaEngine _engine;
    private readonly IFormRuleRepository _formRuleRepo;
    private readonly IFormRepository _formRepo;

    public MassUpdateRecordsCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IRelationshipRepository relRepo,
        IRolePermissionEnforcer enforcer,
        IAuditRepository auditRepo,
        IPipelineTriggerInterceptor triggerInterceptor,
        ITenantUnitOfWork uow,
        IQueryContext queryContext,
        IAppRepository appRepo,
        IMessagePublisher messagePublisher,
        FormulaEngine engine,
        IFormRuleRepository formRuleRepo,
        IFormRepository formRepo)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _relRepo = relRepo;
        _enforcer = enforcer;
        _auditRepo = auditRepo;
        _triggerInterceptor = triggerInterceptor;
        _uow = uow;
        _queryContext = queryContext;
        _appRepo = appRepo;
        _messagePublisher = messagePublisher;
        _engine = engine;
        _formRuleRepo = formRuleRepo;
        _formRepo = formRepo;
    }

    public async Task<int> HandleAsync(MassUpdateRecordsCommand command, CancellationToken ct = default)
    {
        if (command.RecordPublicIds.Count == 0)
            throw new ValidationException(new Dictionary<string, string[]> { ["recordIds"] = ["At least one record ID is required."] });
        if (command.RecordPublicIds.Count > 500)
            throw new ValidationException(new Dictionary<string, string[]> { ["recordIds"] = ["Cannot mass-update more than 500 records at once."] });
        if (command.FieldValues.Count == 0)
            throw new ValidationException(new Dictionary<string, string[]> { ["fieldValues"] = ["At least one field value is required."] });

        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);
        var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);

        var tableFieldIds = new HashSet<long>(fields.Where(f => f.Fid.HasValue).Select(f => (long)f.Fid!.Value));
        var unknownIds = command.FieldValues.Keys.Where(k => !tableFieldIds.Contains(k)).ToList();
        if (unknownIds.Count > 0)
            throw new ValidationException(
                new Dictionary<string, string[]> { ["fields"] = [$"Unknown field IDs: {string.Join(", ", unknownIds)}"] });

        var computedIds = command.FieldValues.Keys
            .Where(k => fields.Any(f => f.Fid.HasValue && (long)f.Fid.Value == k && PhysicalNaming.IsComputedTypeCode(f.TypeCode)))
            .ToList();
        if (computedIds.Count > 0)
            throw new ValidationException(
                new Dictionary<string, string[]> { ["fields"] = [$"Formula fields are read-only and cannot be set: {string.Join(", ", computedIds)}"] });

        var systemIds = command.FieldValues.Keys
            .Where(k => fields.Any(f => f.Fid.HasValue && (long)f.Fid.Value == k && f.IsSystem))
            .ToList();
        if (systemIds.Count > 0)
            throw new ValidationException(
                new Dictionary<string, string[]> { ["fields"] = [$"System fields cannot be mass-updated: {string.Join(", ", systemIds)}"] });

        // Reference fields must point at an existing parent record; a value submitted as a
        // human key is resolved to the parent row Id before any constraint check or persist.
        var effectiveValues = new Dictionary<long, object?>(command.FieldValues);
        var refOverrides = await ReferenceWriteValidator.ValidateAsync(fields, effectiveValues, _tableRepo, _fieldRepo, _recordRepo, _relRepo, ct);
        foreach (var kvp in refOverrides)
            effectiveValues[kvp.Key] = kvp.Value;

        var access = await _enforcer.GetTableAccessAsync(table, fields, ct);
        if (!access.Unrestricted)
        {
            if (access.ModifyScope == RecordScopes.None)
                throw new UnauthorizedActionException("You do not have permission to edit records in this table.");
            var blocked = command.FieldValues.Keys.Where(k => !access.EditableFieldIds.Contains(k)).ToList();
            if (blocked.Count > 0)
                throw new UnauthorizedActionException("You do not have permission to write to one or more of the specified fields.");
            if (access.ViewScope == RecordScopes.OwnRecords || access.ModifyScope == RecordScopes.OwnRecords)
                foreach (var recordId in command.RecordPublicIds)
                    await _enforcer.EnsureRecordOwnedAsync(table, recordId, ct);
        }

        // Resolve every requested record up front — a missing/deleted record is itself a validation
        // failure, not a partial success.
        var idMap = await _recordRepo.GetIdsByPublicIdsMapAsync(table, command.RecordPublicIds, ct);
        var violations = new List<RecordConstraintViolation>();

        // The app's configured Date Formatting doesn't vary per record — fetched once here rather
        // than inside the per-record loop below.
        var app = await _appRepo.GetByIdAsync(table.AppId, ct);
        var dateFormat = AppFormattingSettings.GetDateFormatString(app.Formatting);

        foreach (var recordId in command.RecordPublicIds)
        {
            if (!idMap.ContainsKey(recordId))
                violations.Add(new RecordConstraintViolation(recordId, 0, "NotFound", "Record was not found or has been deleted."));
        }

        var foundIds = command.RecordPublicIds.Where(idMap.ContainsKey).ToList();

        // Unique fields being set to the same value across more than one record are inherently
        // self-colliding — no DB round trip needed, they'd all end up identical. This is the
        // "duplicate within the same request" case called out separately from the normal per-record
        // DB check below.
        var inRequestDuplicateFids = new HashSet<long>();
        if (foundIds.Count > 1)
        {
            foreach (var field in fields.Where(f => f.Fid.HasValue && f.IsUnique && effectiveValues.ContainsKey((long)f.Fid.Value)))
            {
                var fid = (long)field.Fid!.Value;
                var value = effectiveValues[fid];
                var isBlank = value is null || (value is string s && string.IsNullOrWhiteSpace(s));
                if (isBlank || PhysicalNaming.IsRangeTypeCode(field.TypeCode)) continue;

                inRequestDuplicateFids.Add(fid);
                var label = !string.IsNullOrWhiteSpace(field.Label) ? field.Label : field.Name;
                foreach (var recordId in foundIds)
                    violations.Add(new RecordConstraintViolation(recordId, fid, "Unique",
                        $"'{label}' must be unique — setting the same value on {foundIds.Count} records at once would create duplicates."));
            }
        }

        // Required + per-record DB Unique check (RecordConstraintValidator) and Form Rule
        // Require/Prevent Save violations (FormRuleServerValidator), merged into the same
        // per-record loop as the pipeline-trigger snapshot fetch below — oldRecord (needed for
        // both the snapshot and 'changed'/'notChanged' form-rule conditions) is fetched once per
        // record rather than twice. Unique violations already reported above (in-request
        // duplicates) are dropped here to avoid reporting the same field/record twice under two
        // different reasons.
        var recordChanges = new List<PipelineRecordChange>();
        foreach (var recordPublicId in foundIds)
        {
            var beforeValues = new Dictionary<long, object?>();
            var afterValues = new Dictionary<long, object?>();
            var changedFieldIds = new List<long>();
            var oldValuesByFid = new Dictionary<long, object?>();

            try
            {
                var oldRecord = await _recordRepo.GetByPublicIdAsync(table, fields, recordPublicId, ct);
                foreach (var f in fields)
                {
                    if (f.Fid.HasValue)
                    {
                        var colKey = PhysicalNaming.GetPhysicalColumnName(f);
                        var oldVal = oldRecord.TryGetValue(colKey, out var ov) ? ov : null;
                        beforeValues[f.Id] = oldVal;
                        beforeValues[f.Fid.Value] = oldVal;
                        oldValuesByFid[f.Fid.Value] = oldVal;

                        if (effectiveValues.TryGetValue(f.Fid.Value, out var newVal))
                        {
                            afterValues[f.Id] = newVal;
                            afterValues[f.Fid.Value] = newVal;
                            changedFieldIds.Add(f.Id);
                        }
                        else
                        {
                            afterValues[f.Id] = oldVal;
                            afterValues[f.Fid.Value] = oldVal;
                        }
                    }
                }

                recordChanges.Add(new PipelineRecordChange(
                    recordPublicId,
                    beforeValues,
                    afterValues,
                    changedFieldIds,
                    PipelineRecordEventType.Modified
                ));

                var recordViolations = await RecordConstraintValidator.CollectViolationsAsync(
                    table, fields, effectiveValues, _recordRepo, isCreate: false, excludeRecordId: idMap[recordPublicId], ct, recordPublicId, appDateFormat: dateFormat);
                violations.AddRange(recordViolations.Where(v => !(v.ConstraintType == "Unique" && inRequestDuplicateFids.Contains(v.FieldId))));

                var formRuleViolations = await FormRuleServerValidator.CollectViolationsAsync(
                    table, fields, effectiveValues, oldValuesByFid, _queryContext.TenantRole,
                    _formRuleRepo, _formRepo, _tableRepo, _fieldRepo, _recordRepo, _engine, ct, recordPublicId);
                violations.AddRange(formRuleViolations);
            }
            catch (NotFoundException)
            {
                // Skip if not found
            }
        }

        if (violations.Count > 0)
            throw new RecordConstraintViolationException(violations);

        var indexMessages = new List<SearchIndexMessage>();
        int affected;
        await _uow.BeginAsync(ct);
        try
        {
            if (recordChanges.Count > 0)
            {
                await _triggerInterceptor.InterceptBulkAsync(
                    table, fields, recordChanges, Guid.NewGuid(), Guid.NewGuid(), _queryContext.UserId, ct);
            }

            affected = await _recordRepo.MassUpdateAsync(table, fields, idMap.Values.ToList(), effectiveValues, ct, indexMessages.Add, _uow.Transaction);

            await _auditRepo.LogActivityAsync(
                AuditActions.Updated, AuditEntityTypes.Record, table.PublicId.ToString(),
                $"{affected} record(s) mass-updated in {table.Name}", appId: table.AppId, ct: ct);

            await _uow.CommitAsync(ct);
        }
        catch
        {
            await _uow.RollbackAsync(CancellationToken.None);
            throw;
        }
        if (indexMessages.Count > 0)
            _ = _messagePublisher.PublishBatchAsync(indexMessages, default);
        return affected;
    }
}
