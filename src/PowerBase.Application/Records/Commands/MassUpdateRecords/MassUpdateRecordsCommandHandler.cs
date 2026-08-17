using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Records.Commands.MassUpdateRecords;

public class MassUpdateRecordsCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IRolePermissionEnforcer _enforcer;
    private readonly IAuditRepository _auditRepo;

    public MassUpdateRecordsCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IRolePermissionEnforcer enforcer,
        IAuditRepository auditRepo)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _enforcer = enforcer;
        _auditRepo = auditRepo;
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
            foreach (var field in fields.Where(f => f.Fid.HasValue && f.IsUnique && command.FieldValues.ContainsKey((long)f.Fid.Value)))
            {
                var fid = (long)field.Fid!.Value;
                var value = command.FieldValues[fid];
                var isBlank = value is null || (value is string s && string.IsNullOrWhiteSpace(s));
                if (isBlank || PhysicalNaming.IsRangeTypeCode(field.TypeCode)) continue;

                inRequestDuplicateFids.Add(fid);
                var label = field.Label ?? field.Name;
                foreach (var recordId in foundIds)
                    violations.Add(new RecordConstraintViolation(recordId, fid, "Unique",
                        $"'{label}' must be unique — setting the same value on {foundIds.Count} records at once would create duplicates."));
            }
        }

        // Required + per-record DB Unique check, reusing the same validator every other write path uses.
        // Unique violations already reported above (in-request duplicates) are dropped here to avoid
        // reporting the same field/record twice under two different reasons.
        foreach (var recordId in foundIds)
        {
            var recordViolations = await RecordConstraintValidator.CollectViolationsAsync(
                table, fields, command.FieldValues, _recordRepo, isCreate: false, excludeRecordId: idMap[recordId], ct, recordId);
            violations.AddRange(recordViolations.Where(v => !(v.ConstraintType == "Unique" && inRequestDuplicateFids.Contains(v.FieldId))));
        }

        if (violations.Count > 0)
            throw new RecordConstraintViolationException(violations);

        var affected = await _recordRepo.MassUpdateAsync(table, fields, idMap.Values.ToList(), command.FieldValues, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.Updated, AuditEntityTypes.Record, table.PublicId.ToString(),
            $"{affected} record(s) mass-updated in {table.Name}", appId: table.AppId, ct: ct);

        return affected;
    }
}
