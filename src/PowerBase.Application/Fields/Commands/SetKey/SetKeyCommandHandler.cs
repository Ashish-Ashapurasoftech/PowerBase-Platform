using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Fields.Commands.SetKey;

/// <summary>
/// Designates a table's key field (or resets to the default Record ID#), cascading the change
/// through every relationship where the table is the parent: each child's Reference field is
/// migrated to store the new key's value, dependent Lookup/Summary settings and the Relationship
/// row are repointed at it, and the old reference field is demoted to a Lookup recording its
/// former value. Milestone 1 scope: stored scalar key fields only (Text/Number/Currency/Percent/
/// Rating/Date) — Formula/Lookup keys are a later milestone (they need a materialized column).
/// </summary>
public class SetKeyCommandHandler
{
    private static readonly HashSet<string> EligibleScalarTypes =
        ["Text", "Number", "Currency", "Percent", "Rating", "Date"];
    private const int RecordIdFid = 3;
    private const int MaxCascadeRows = 100_000; // v1 safety cap — see plan's "no background job infra" note.

    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IFieldTypeRepository _fieldTypeRepo;
    private readonly IRelationshipRepository _relRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly ISchemaEngineService _schemaEngine;
    private readonly RelationshipFieldFactory _fieldFactory;
    private readonly IAuditRepository _auditRepo;

    public SetKeyCommandHandler(
        IAppTableRepository tableRepo, IAppFieldRepository fieldRepo, IFieldTypeRepository fieldTypeRepo,
        IRelationshipRepository relRepo, IRecordRepository recordRepo, ISchemaEngineService schemaEngine,
        RelationshipFieldFactory fieldFactory, IAuditRepository auditRepo)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _fieldTypeRepo = fieldTypeRepo;
        _relRepo = relRepo;
        _recordRepo = recordRepo;
        _schemaEngine = schemaEngine;
        _fieldFactory = fieldFactory;
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

        var parentRels = await _relRepo.ListByParentTableAsync(table.Id, ct);
        if (parentRels.Count > 0 && !command.Force)
            throw new ConflictException(
                $"'{table.Name}' has relationships. Changing the key will update the reference on every related record. " +
                "Confirm to proceed.");

        foreach (var rel in parentRels)
            await CascadeRewireAsync(table, oldKeyField, newKeyField, rel, ct);

        // A key field must be guaranteed unique + always populated (Record ID# already is both).
        if (newKeyField is not null)
        {
            await _fieldRepo.UpdateAsync(
                newKeyField.PublicId, table.Id,
                newKeyField.Name, newKeyField.Label, newKeyField.Description,
                isRequired: true, newKeyField.DefaultValue,
                newKeyField.IsSearchable, newKeyField.IsSortable,
                newKeyField.IsFilterable, newKeyField.IsReportable, newKeyField.IsAuditable,
                isUnique: true, isEncrypted: newKeyField.IsEncrypted, newKeyField.Settings, ct);
            await _schemaEngine.SetUniqueAsync(table, newKeyField, true, ct);
        }

        await _tableRepo.SetKeyFieldAsync(table.Id, newKeyField?.Id, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.SchemaChanged, AuditEntityTypes.AppField, table.PublicId.ToString(),
            newKeyField is not null
                ? $"Key field for '{table.Name}' changed to '{newKeyField.Name}'"
                : $"Key field for '{table.Name}' reset to Record ID#",
            appId: table.AppId, ct: ct);
    }

    /// <summary>Rewires one parent-side relationship: migrates the child's reference storage from the
    /// old key's values to the new key's values, repoints dependent Lookup/Summary settings and the
    /// Relationship row, and demotes the old reference field to a historical Lookup.</summary>
    private async Task CascadeRewireAsync(AppTable parentTable, AppField? oldKeyField, AppField? newKeyField, Relationship rel, CancellationToken ct)
    {
        var childTable = await _tableRepo.GetByIdAsync(rel.ChildTableId, ct);
        var childFields = await _fieldRepo.ListByTableAsync(childTable.Id, ct);
        var oldReferenceField = childFields.FirstOrDefault(f => f.Id == rel.ReferenceFieldId)
            ?? throw new InvalidOperationException($"Relationship {rel.Id}: reference field not found.");
        var oldRefColumn = PhysicalNaming.ColumnName(oldReferenceField.Fid!.Value);

        // Build the old-value → new-value mapping for every parent row (bounded — see MaxCascadeRows).
        var oldKeyColumn = KeyFieldResolver.ColumnName(oldKeyField);
        var newKeyColumn = KeyFieldResolver.ColumnName(newKeyField);
        var parentRows = await _recordRepo.ListAsync(parentTable, Array.Empty<AppField>(), 1, MaxCascadeRows, ct: ct);
        var rowIds = parentRows.Select(r => Convert.ToInt64(r["Id"])).ToList();

        var oldValues = oldKeyField is null
            ? rowIds.ToDictionary(id => id, object? (id) => id)
            : await _recordRepo.GetColumnValuesByIdsAsync(parentTable, oldKeyColumn, rowIds, ct);
        var newValues = newKeyField is null
            ? rowIds.ToDictionary(id => id, object? (id) => id)
            : await _recordRepo.GetColumnValuesByIdsAsync(parentTable, newKeyColumn, rowIds, ct);

        var mapping = new Dictionary<object, object?>();
        foreach (var id in rowIds)
            if (oldValues.TryGetValue(id, out var oldVal) && oldVal is not null)
                mapping[oldVal] = newValues.TryGetValue(id, out var newVal) ? newVal : null;

        // 1. Determine the target Reference field: promote a matching existing Lookup in place
        //    (reuses its Fid/name; still needs a physical column since Lookups are compute-on-read),
        //    else create a new field typed to match the key and convert it.
        var refType = await _fieldTypeRepo.GetByCodeAsync("Reference", ct) ?? throw new NotFoundException("FieldType", "Reference");
        var refSettingsJson = RelationshipFieldFactory.Serialize(new ReferenceSettings { RelationshipId = rel.Id, ParentTableId = parentTable.Id });

        var candidateLookup = newKeyField is not null
            ? childFields.FirstOrDefault(f => f.TypeCode == "Lookup"
                && FormulaTypeMap.ParseLookupSettings(f.Settings) is { } ls
                && ls.SourceFid == newKeyField.Fid && ls.SourceTableId == parentTable.Id)
            : null;

        AppField newRefField;
        if (candidateLookup is not null)
        {
            var creationType = await _fieldTypeRepo.GetByCodeAsync(newKeyField!.TypeCode, ct)
                ?? throw new NotFoundException("FieldType", newKeyField.TypeCode);
            var col = PhysicalNaming.ColumnName(candidateLookup.Fid!.Value);
            await _fieldRepo.UpdatePhysicalColumnNameAsync(candidateLookup.Id, col, ct);
            candidateLookup.PhysicalColumnName = col;
            candidateLookup.TypeCode = creationType.Code;
            candidateLookup.FieldTypeId = creationType.Id;
            await _schemaEngine.AddColumnAsync(childTable, candidateLookup, ct);

            await _fieldRepo.UpdateFieldTypeAsync(candidateLookup.Id, refType.Id, refSettingsJson, isRequired: false, ct);
            newRefField = candidateLookup;
        }
        else
        {
            var creationTypeCode = newKeyField?.TypeCode ?? "Reference";
            var name = $"Related {parentTable.SingularLabel ?? parentTable.Name}";
            var created = await _fieldFactory.CreateAsync(childTable, creationTypeCode, name, null, isRequired: false,
                new ReferenceSettings { ParentTableId = parentTable.Id }, ct);
            if (creationTypeCode != "Reference")
            {
                await _fieldRepo.UpdateFieldTypeAsync(created.Id, refType.Id, refSettingsJson, isRequired: false, ct);
                created.TypeCode = refType.Code;
            }
            newRefField = created;
        }

        // 2. Migrate data: rewrite every child row's reference value.
        var newRefColumn = PhysicalNaming.ColumnName(newRefField.Fid!.Value);
        await _recordRepo.RewriteReferenceColumnAsync(childTable, oldRefColumn, newRefColumn, mapping, ct);

        // 3. Repoint every other Lookup in the child whose ReferenceFid was the old reference.
        foreach (var lu in childFields.Where(f => f.TypeCode == "Lookup" && f.Id != newRefField.Id))
        {
            var ls = FormulaTypeMap.ParseLookupSettings(lu.Settings);
            if (ls is null || ls.ReferenceFid != oldReferenceField.Fid) continue;
            var updated = new LookupSettings
            {
                RelationshipId = ls.RelationshipId, ReferenceFid = newRefField.Fid,
                SourceTableId = ls.SourceTableId, SourceFid = ls.SourceFid, SourceTypeCode = ls.SourceTypeCode,
            };
            await _fieldRepo.UpdateSettingsAsync(lu.Id, RelationshipFieldFactory.Serialize(updated), ct);
        }

        // 4. Repoint every Summary on the parent whose ReferenceFid was the old reference.
        var parentFields = await _fieldRepo.ListByTableAsync(parentTable.Id, ct);
        foreach (var sm in parentFields.Where(f => f.TypeCode == "Summary"))
        {
            var ss = FormulaTypeMap.ParseSummarySettings(sm.Settings);
            if (ss is null || ss.ReferenceFid != oldReferenceField.Fid) continue;
            var updated = new SummarySettings
            {
                RelationshipId = ss.RelationshipId, ChildTableId = ss.ChildTableId, ReferenceFid = newRefField.Fid,
                Function = ss.Function, TargetFid = ss.TargetFid, TargetTypeCode = ss.TargetTypeCode, FilterTree = ss.FilterTree,
            };
            await _fieldRepo.UpdateSettingsAsync(sm.Id, RelationshipFieldFactory.Serialize(updated), ct);
        }

        // 5. Update the Relationship row to point at the new reference field.
        await _relRepo.UpdateReferenceFieldAsync(rel.Id, newRefField.Id, newRefField.Fid!.Value, ct);

        // 6. Demote the old reference field to a Lookup recording its former (pre-migration) key value,
        //    unless it's the very field we just promoted/reused.
        if (oldReferenceField.Id != newRefField.Id)
        {
            var lookupType = await _fieldTypeRepo.GetByCodeAsync("Lookup", ct) ?? throw new NotFoundException("FieldType", "Lookup");
            var demotedSettings = new LookupSettings
            {
                RelationshipId = rel.Id, ReferenceFid = newRefField.Fid, SourceTableId = parentTable.Id,
                SourceFid = oldKeyField?.Fid ?? RecordIdFid, SourceTypeCode = oldKeyField?.TypeCode ?? "Number",
            };
            await _fieldRepo.UpdateFieldTypeAsync(oldReferenceField.Id, lookupType.Id, RelationshipFieldFactory.Serialize(demotedSettings), isRequired: false, ct);
        }
    }
}
