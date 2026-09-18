using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Fields.Commands.SetKey;

/// <summary>
/// Designates a table's key field (or resets it to the default Record ID#). The key field is a
/// display/identity choice only — it drives which column the reference picker, grid, filter, and
/// report show for records of this table. It does <b>not</b> change how relationships store their
/// link: a child's Reference column always holds the parent's internal row Id, which never
/// changes, so switching the key field can never break an existing reference. Eligible key
/// fields are stored scalar types (Text/Number/Currency/Percent/Rating/Date); the field is forced
/// unique + always-populated to match Record ID#.
/// </summary>
public class SetKeyCommandHandler
{
    private static readonly HashSet<string> EligibleScalarTypes =
        ["Text", "Number", "Currency", "Percent", "Rating", "Date"];
    private const int RecordIdFid = 3;

    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly ISchemaEngineService _schemaEngine;
    private readonly IRelationshipRepository _relRepo;
    private readonly RelationshipKeyCarryOverService _carryOver;
    private readonly IAuditRepository _auditRepo;

    public SetKeyCommandHandler(
        IAppTableRepository tableRepo, IAppFieldRepository fieldRepo, IRecordRepository recordRepo,
        ISchemaEngineService schemaEngine, IRelationshipRepository relRepo,
        RelationshipKeyCarryOverService carryOver, IAuditRepository auditRepo)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _schemaEngine = schemaEngine;
        _relRepo = relRepo;
        _carryOver = carryOver;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(SetKeyCommand command, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);
        var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);
        var oldKeyField = await KeyFieldResolver.ResolveAsync(table, _fieldRepo, ct);

        AppField? newKeyField = null;
        var resettingToDefault = command.FieldFid is null or RecordIdFid;
        if (!resettingToDefault)
        {
            newKeyField = fields.FirstOrDefault(f => f.Fid == command.FieldFid)
                ?? throw new NotFoundException("Field", command.FieldFid!.Value);

            if (newKeyField.IsSystem || !EligibleScalarTypes.Contains(newKeyField.TypeCode))
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["fieldFid"] = ["The selected field type cannot be used as a key field."],
                });

            if (oldKeyField?.Id == newKeyField.Id)
                throw new ValidationException(new Dictionary<string, string[]> { ["fieldFid"] = ["This field is already the key."] });

            if (await _recordRepo.HasDuplicatesAsync(table, newKeyField, ct))
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["fieldFid"] = [$"Error attempting to change the key field to Field ID: {newKeyField.Fid}. The selected field contains non-unique values."],
                });

            if (await _recordRepo.HasNullsAsync(table, newKeyField, ct))
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["fieldFid"] = [$"Error attempting to change the key field to Field ID: {newKeyField.Fid}. The selected field contains empty values."],
                });
        }
        else if (oldKeyField is null)
        {
            throw new ValidationException(new Dictionary<string, string[]> { ["fieldFid"] = ["This field is already the key."] });
        }

        // A key field must be guaranteed unique + always populated (Record ID# already is both).
        if (newKeyField is not null)
        {
            await _schemaEngine.SetUniqueAsync(table, newKeyField, true, ct);
            await _fieldRepo.UpdateAsync(
                newKeyField.PublicId, table.Id,
                newKeyField.Label, newKeyField.Description,
                isRequired: true, newKeyField.DefaultValue,
                newKeyField.IsSearchable, newKeyField.IsSortable,
                newKeyField.IsFilterable, newKeyField.IsReportable, newKeyField.IsAuditable,
                isUnique: true, isEncrypted: newKeyField.IsEncrypted, isAutoFill: newKeyField.IsAutoFill, newKeyField.Settings, ct);
        }

        await _tableRepo.SetKeyFieldAsync(table.Id, newKeyField?.Id, ct);

        // Cascade to every relationship where this table is the parent and that relies on this
        // table-wide default (no per-relationship override of its own — see
        // KeyFieldResolver.ResolveDisplayKey's precedence). A relationship with its own override is
        // untouched: that override always wins over Set Key, so nothing about it actually changed.
        // Same carry-over/removal auto-chain as UpdateDisplayKeyCommandHandler, just applied per
        // affected relationship instead of to one — Record ID# is a normal field here too, via the
        // same fid-3 fallback.
        var effectiveOldKeyField = oldKeyField
            ?? fields.FirstOrDefault(f => f.IsSystem && f.Fid == RecordIdFid)
            ?? fields.FirstOrDefault(f => f.Fid == RecordIdFid);
        var effectiveNewKeyField = newKeyField
            ?? fields.FirstOrDefault(f => f.IsSystem && f.Fid == RecordIdFid)
            ?? fields.FirstOrDefault(f => f.Fid == RecordIdFid);

        var updatedRelationshipCount = 0;
        if (effectiveOldKeyField?.Id != effectiveNewKeyField?.Id)
        {
            var affectedRelationships = (await _relRepo.ListByParentTableAsync(table.Id, ct))
                .Where(r => r.DisplayKeyFieldId is null);
            foreach (var affectedRel in affectedRelationships)
            {
                var childTable = await _tableRepo.GetByIdAsync(affectedRel.ChildTableId, ct);
                var result = await _carryOver.ApplyAsync(affectedRel, table, childTable, effectiveOldKeyField, effectiveNewKeyField, ct);
                if (result.CarriedOverLabel is not null || result.RemovedLabel is not null)
                    updatedRelationshipCount++;
            }
        }

        var auditMessage = newKeyField is not null
            ? $"Key field for '{table.Name}' changed to '{newKeyField.Name}'"
            : $"Key field for '{table.Name}' reset to Record ID#";
        if (updatedRelationshipCount > 0)
            auditMessage += $" ({updatedRelationshipCount} relationship{(updatedRelationshipCount == 1 ? "" : "s")} updated)";
        await _auditRepo.LogActivityAsync(
            AuditActions.SchemaChanged, AuditEntityTypes.AppField, table.PublicId.ToString(),
            auditMessage, appId: table.AppId, ct: ct);
    }
}
