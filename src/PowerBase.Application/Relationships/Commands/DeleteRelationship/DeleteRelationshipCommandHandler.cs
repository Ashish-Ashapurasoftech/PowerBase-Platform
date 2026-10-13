using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.FieldReferences;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Relationships.Commands.DeleteRelationship;

public class DeleteRelationshipCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRelationshipRepository _relRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly IReportRepository? _reportRepo;
    private readonly IFieldReferenceIndexer _refIndexer;
    private readonly IFieldTypeRepository? _fieldTypeRepo;
    private readonly ISchemaEngineService? _schemaEngine;

    // Stored types a converted field is put back to as itself, filled from the parent's key text. A Formula is put back
    // as the formula (it recomputes). Any other original type comes back as a Text field holding that text.
    private static readonly HashSet<string> RestorableTypeCodes = new(StringComparer.OrdinalIgnoreCase)
        { "Text", "MultiSelect", "Number", "Currency", "Date", "DateTime" };

    private enum RestoreOutcome { Restored, RevertToNumber, Failed }

    public DeleteRelationshipCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRelationshipRepository relRepo,
        IRecordRepository recordRepo,
        IAuditRepository auditRepo,
        IReportRepository? reportRepo = null,
        IFieldReferenceIndexer? refIndexer = null,
        IFieldTypeRepository? fieldTypeRepo = null,
        ISchemaEngineService? schemaEngine = null)
    {
        _fieldTypeRepo = fieldTypeRepo;
        _schemaEngine = schemaEngine;
        _reportRepo = reportRepo;
        _refIndexer = refIndexer ?? NullFieldReferenceIndexer.Instance;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _relRepo = relRepo;
        _recordRepo = recordRepo;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(DeleteRelationshipCommand command, CancellationToken ct = default)
    {
        var rel = await _relRepo.GetByPublicIdAsync(command.RelationshipPublicId, ct)
            ?? throw new NotFoundException("Relationship", command.RelationshipPublicId);

        var child = await _tableRepo.GetByIdAsync(rel.ChildTableId, ct);
        var parent = await _tableRepo.GetByIdAsync(rel.ParentTableId, ct);
        var childFields = await _fieldRepo.ListByTableAsync(child.Id, ct);
        var parentFields = await _fieldRepo.ListByTableAsync(parent.Id, ct);

        var refField = childFields.FirstOrDefault(f => f.Id == rel.ReferenceFieldId);

        // Restrict: deleting the reference field would orphan child links.
        if (!command.Force && refField is not null && await _recordRepo.HasAnyDataAsync(child, refField, ct))
            throw new ConflictException(
                $"This relationship is in use — some {child.Name} records reference a {parent.Name}. " +
                "Clear those references first, or force-delete the relationship.");

        // Soft-delete the participating fields.
        if (refField is not null)
        {
            if (rel.ReferenceFieldIsExisting)
            {
                // The reference field existed before this relationship was created. Turn it back into a real field
                // holding what the user sees now (the parent's key text), so deleting the relationship loses nothing.
                var (outcome, why) = command.Restore == OriginalFieldRestore.None
                    ? (RestoreOutcome.RevertToNumber, (string?)null)
                    : await TryRestoreOriginalFieldAsync(rel, child, parent, childFields, parentFields, refField, ct);
                if (outcome == RestoreOutcome.Failed)
                {
                    // Nothing was changed. Strict refuses (the text would be lost); best effort reverts to Number as before.
                    if (command.Restore == OriginalFieldRestore.Strict)
                        throw new ConflictException(
                            $"Can't delete this relationship without losing the data in '{refField.Label ?? refField.Name}': {why}");
                    Console.WriteLine($"[DeleteRelationship] '{refField.Name}' (fid {refField.Fid}) reverted to Number, not restored: {why}");
                }
                if (outcome != RestoreOutcome.Restored)
                    await _fieldRepo.RevertToNumberFieldAsync(refField.Id, ct);
            }
            else
            {
                // The reference field was auto-created for this relationship — delete it.
                await _fieldRepo.DeleteAsync(refField.PublicId, child.Id, ct);
            }
        }

        foreach (var f in childFields.Where(f => f.TypeCode == "Lookup"
            && FormulaTypeMap.ParseLookupSettings(f.Settings)?.RelationshipId == rel.Id))
            await _fieldRepo.DeleteAsync(f.PublicId, child.Id, ct);

        foreach (var f in parentFields.Where(f => f.TypeCode == "Summary"
            && FormulaTypeMap.ParseSummarySettings(f.Settings)?.RelationshipId == rel.Id))
            await _fieldRepo.DeleteAsync(f.PublicId, parent.Id, ct);

        foreach (var f in parentFields.Where(f => f.TypeCode == "ReportLink"
            && FormulaTypeMap.ParseReportLinkSettings(f.Settings)?.RelationshipId == rel.Id))
            await _fieldRepo.DeleteAsync(f.PublicId, parent.Id, ct);

        foreach (var f in parentFields.Where(f => f.TypeCode == RelationshipFieldFactory.FormulaUrlTypeCode
            && FormulaTypeMap.ParseFormulaUrlSettings(f.Settings)?.RelationshipId == rel.Id))
            await _fieldRepo.DeleteAsync(f.PublicId, parent.Id, ct);

        // The hidden "Embedded for {Parent}" report(s) the wizard created for this relationship go with it.
        if (_reportRepo is not null)
        {
            foreach (var reportId in await _reportRepo.ListPublicIdsByRelationshipAsync(rel.PublicId, ct))
            {
                await _reportRepo.DeleteAsync(reportId, ct);
                await _refIndexer.RemoveSourceAsync(FieldReferenceSourceTypes.Report, reportId, ct);
            }
        }

        await _relRepo.SoftDeleteAsync(rel.PublicId, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.SchemaChanged, AuditEntityTypes.AppField, rel.Id.ToString(),
            $"Relationship deleted: {child.Name} → {parent.Name}", appId: rel.AppId, ct: ct);
    }

    /// <summary>
    /// Turns a Reference that was converted from an existing field back into a real field, so deleting the relationship
    /// loses nothing:
    ///   • it was a stored type we can rebuild (Text, Number, Date…) → that type again, each row holding the parent's key
    ///     text (a row whose reference was NULL gets the value kept in the hidden "(original)" field);
    ///   • it was a Formula → the formula again (it recomputes; its physical column is dropped);
    ///   • any other type, or one recorded before this was tracked with a non-numeric key → a Text field with that text;
    ///   • it was an already-numeric field holding Record IDs (metadata-only conversion) → RevertToNumber, which is exact.
    /// Failed (with the reason) means the schema engine couldn't do it (e.g. SQL-enclave encryption, a blocking index);
    /// nothing has been changed in that case.
    /// </summary>
    private async Task<(RestoreOutcome Outcome, string? Why)> TryRestoreOriginalFieldAsync(
        Relationship rel, AppTable child, AppTable parent,
        IReadOnlyList<AppField> childFields, IReadOnlyList<AppField> parentFields, AppField refField, CancellationToken ct)
    {
        if (_fieldTypeRepo is null || _schemaEngine is null) return (RestoreOutcome.RevertToNumber, null);   // legacy construction

        var settings = FormulaTypeMap.ParseReferenceSettings(refField.Settings);
        var displayKey = KeyFieldResolver.ResolveDisplayKey(rel, parent, parentFields);
        var originalTypeCode = settings?.OriginalTypeCode;
        if (originalTypeCode is null)
        {
            // Not recorded: a numeric key means the field was already numeric (Record IDs) — Number is exactly right.
            // Otherwise it held the parent's key text (an older relationship), so give that back as Text.
            if (displayKey is null || displayKey.TypeCode == nameof(Domain.Enums.FieldTypeCode.Number))
                return (RestoreOutcome.RevertToNumber, null);
            originalTypeCode = nameof(Domain.Enums.FieldTypeCode.Text);
        }

        var originalField = settings?.OriginalFid is int ofid ? childFields.FirstOrDefault(f => f.Fid == ofid) : null;

        if (originalTypeCode.StartsWith("Formula", StringComparison.OrdinalIgnoreCase))
        {
            var formulaType = await _fieldTypeRepo.GetByCodeAsync(originalTypeCode, ct);
            if (formulaType is null) return (RestoreOutcome.Failed, $"field type '{originalTypeCode}' not found");
            // A Formula has no stored values: the formula itself is the data, so putting it back loses nothing.
            await _schemaEngine.DropColumnIfExistsAsync(child, refField, ct);
            await _fieldRepo.UpdatePhysicalColumnNameAsync(refField.Id, null!, ct);
            await _fieldRepo.UpdateFieldTypeAsync(refField.Id, formulaType.Id, settings?.OriginalSettings, refField.IsRequired, ct);
            if (originalField is not null) await _fieldRepo.DeleteAsync(originalField.PublicId, child.Id, ct);
            return (RestoreOutcome.Restored, null);
        }

        // Anything that isn't a stored type we can rebuild comes back as Text (its settings no longer apply).
        var rebuildAsItself = RestorableTypeCodes.Contains(originalTypeCode);
        var restoredType = await _fieldTypeRepo.GetByCodeAsync(
            rebuildAsItself ? originalTypeCode : nameof(Domain.Enums.FieldTypeCode.Text), ct);
        if (restoredType is null) return (RestoreOutcome.Failed, $"field type '{originalTypeCode}' not found");
        var restoredSettings = rebuildAsItself ? settings?.OriginalSettings : null;

        var originalCol = originalField is null ? null : PhysicalNaming.ColumnName(originalField.Fid!.Value);
        var refCol = PhysicalNaming.ColumnName(refField.Fid!.Value);

        var rows = await _recordRepo.ListAllRowsDecryptedAsync(child, childFields, ct);
        var parentIds = new HashSet<long>();
        foreach (var row in rows)
            if (row.TryGetValue(refCol, out var v) && v is not null && long.TryParse(v.ToString(), out var pid)) parentIds.Add(pid);
        var parentRows = parentIds.Count == 0
            ? new Dictionary<long, IReadOnlyDictionary<string, object?>>()
            : (await _recordRepo.GetRowsByIdsAsync(parent, parentFields, parentIds, ct)).ToDictionary(p => p.Key, p => p.Value);

        // The value the parent's key shows as: Record ID# when the relationship has no other display key.
        var keyCol = KeyFieldResolver.ColumnName(displayKey);
        var keyFidCol = displayKey?.Fid is int kf ? PhysicalNaming.ColumnName(kf) : null;

        var textByRow = new Dictionary<long, string>();
        foreach (var row in rows)
        {
            if (!row.TryGetValue("Id", out var idVal) || idVal is null) continue;
            string? text = null;
            if (row.TryGetValue(refCol, out var rv) && rv is not null && long.TryParse(rv.ToString(), out var parentId))
            {
                if (displayKey is null) text = parentId.ToString();
                else if (parentRows.TryGetValue(parentId, out var prow))
                {
                    if (!prow.TryGetValue(keyCol, out var keyValue) && keyFidCol is not null) prow.TryGetValue(keyFidCol, out keyValue);
                    if (keyValue is not null) text = KeyFieldResolver.FormatForSubmit(keyValue);
                }
            }
            if (string.IsNullOrEmpty(text) && originalCol is not null && row.TryGetValue(originalCol, out var ov))
                text = ov?.ToString();
            if (string.IsNullOrEmpty(text)) continue;

            // A Text - Multiple Choice column holds a JSON array of the chosen values.
            textByRow[Convert.ToInt64(idVal)] = restoredType.Code.Equals("MultiSelect", StringComparison.OrdinalIgnoreCase)
                ? System.Text.Json.JsonSerializer.Serialize(new[] { text })
                : text;
        }

        var restoredField = new AppField
        {
            Id = refField.Id, Fid = refField.Fid, AppTableId = refField.AppTableId, Name = refField.Name,
            FieldTypeId = restoredType.Id, TypeCode = restoredType.Code, PhysicalColumnName = refField.PhysicalColumnName,
            IsEncrypted = refField.IsEncrypted,
        };
        // In an encrypted app the restored column holds ciphertext, like every other column of the table.
        var storedByRow = await _recordRepo.EncryptColumnTextsAsync(child, restoredField, textByRow, ct);
        if (!await _schemaEngine.RestoreColumnFromReferenceAsync(child, restoredField, storedByRow, ct))
            return (RestoreOutcome.Failed, "the column couldn't be rebuilt (SQL-enclave encryption, or an index/constraint on it — see the [RestoreColumnFromReference] line in the API log)");

        await _fieldRepo.UpdateFieldTypeAsync(refField.Id, restoredType.Id, restoredSettings, refField.IsRequired, ct);
        // Its values now live in the restored field, so the hidden helper has nothing left to hold.
        if (originalField is not null) await _fieldRepo.DeleteAsync(originalField.PublicId, child.Id, ct);
        return (RestoreOutcome.Restored, null);
    }
}
