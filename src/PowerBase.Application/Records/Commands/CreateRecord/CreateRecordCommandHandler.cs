using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Records.Commands.CreateRecord;

public class CreateRecordCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IRolePermissionEnforcer _enforcer;
    private readonly IAuditRepository _auditRepo;
    private readonly IFormulaDefaultResolver _formulaDefaults;

    public CreateRecordCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IRolePermissionEnforcer enforcer,
        IAuditRepository auditRepo,
        IFormulaDefaultResolver formulaDefaults)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _enforcer = enforcer;
        _auditRepo = auditRepo;
        _formulaDefaults = formulaDefaults;
    }

    public async Task<RecordResult> HandleAsync(CreateRecordCommand command, CancellationToken ct = default)
    {
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

        var access = await _enforcer.GetTableAccessAsync(table, fields, ct);
        if (!access.Unrestricted)
        {
            if (!access.CanAdd)
                throw new UnauthorizedActionException("You do not have permission to add records to this table.");
            var blocked = command.FieldValues.Keys.Where(k => !access.EditableFieldIds.Contains(k)).ToList();
            if (blocked.Count > 0)
                throw new UnauthorizedActionException("You do not have permission to write to one or more of the specified fields.");
        }

        // Reference fields must point at an existing parent record.
        await ReferenceWriteValidator.ValidateAsync(fields, command.FieldValues, _tableRepo, _fieldRepo, _recordRepo, ct);

        // Inject default values for fields that were not submitted (e.g. hidden None-access fields).
        // This ensures required fields with defaults are always populated regardless of role restrictions.
        var effectiveValues = new Dictionary<long, object?>(command.FieldValues);
        foreach (var field in fields)
        {
            if (field.IsSystem || field.IsDeleted || !field.Fid.HasValue || PhysicalNaming.IsComputedTypeCode(field.TypeCode)) continue;
            if (effectiveValues.ContainsKey((long)field.Fid.Value)) continue;
            if (!string.IsNullOrWhiteSpace(field.DefaultValue))
                effectiveValues[(long)field.Fid.Value] = _formulaDefaults.Resolve(field.DefaultValue, field, fields, effectiveValues, table);
        }

        var publicId = await _recordRepo.CreateAsync(table, fields, effectiveValues, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.Created, AuditEntityTypes.Record, publicId.ToString(), $"Record added in {table.Name} with ID {publicId}", appId: table.AppId, ct: ct);

        await _tableRepo.IncrementRecordCountAsync(table.Id, ct);

        var fieldData = new Dictionary<string, object?>();
        foreach (var field in fields.Where(f => f.Fid.HasValue && effectiveValues.ContainsKey((long)f.Fid.Value)))
            fieldData[field.Fid!.Value.ToString()] = effectiveValues[(long)field.Fid.Value];

        return new RecordResult
        {
            Id = publicId,
            CreatedOn = DateTime.UtcNow,
            ModifiedOn = null,
            Fields = fieldData,
        };
    }
}
