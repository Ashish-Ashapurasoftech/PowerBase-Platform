using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Relationships.Commands.CreateRelationship;

public class CreateRelationshipCommandHandler
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IFieldTypeRepository _fieldTypeRepo;
    private readonly IRelationshipRepository _relRepo;
    private readonly RelationshipFieldFactory _fieldFactory;
    private readonly IAuditRepository _auditRepo;
    private readonly IAppRepository _appRepo;
    private readonly ISchemaEngineService _schemaEngine;
    private readonly IRecordRepository _recordRepo;
    private readonly PowerBase.Application.Formulas.IFormulaProjector _formulaProjector;
    private readonly IRelationalProjector _relationalProjector;

    public CreateRelationshipCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IFieldTypeRepository fieldTypeRepo,
        IRelationshipRepository relRepo,
        RelationshipFieldFactory fieldFactory,
        IAuditRepository auditRepo,
        IAppRepository appRepo,
        ISchemaEngineService schemaEngine,
        IRecordRepository recordRepo,
        PowerBase.Application.Formulas.IFormulaProjector formulaProjector,
        IRelationalProjector relationalProjector)
    {
        _schemaEngine = schemaEngine;
        _recordRepo = recordRepo;
        _formulaProjector = formulaProjector;
        _relationalProjector = relationalProjector;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _fieldTypeRepo = fieldTypeRepo;
        _relRepo = relRepo;
        _fieldFactory = fieldFactory;
        _auditRepo = auditRepo;
        _appRepo = appRepo;
    }

    public async Task<RelationshipDto> HandleAsync(CreateRelationshipCommand command, CancellationToken ct = default)
    {
        // A label is only required when we're creating a brand-new reference field. When reusing an existing
        // child field (ReferenceFieldFid set) the field already has one.
        if (command.ReferenceFieldFid is null && string.IsNullOrWhiteSpace(command.ReferenceFieldLabel))
            throw new ValidationException(new Dictionary<string, string[]> { ["referenceFieldLabel"] = ["Reference field label is required."] });

        var parent = await _tableRepo.GetByPublicIdAsync(command.ParentTablePublicId, ct);
        var child = await _tableRepo.GetByPublicIdAsync(command.ChildTablePublicId, ct);
        if (parent.AppId != child.AppId)
            throw new ValidationException(new Dictionary<string, string[]> { ["tables"] = ["Both tables must belong to the same app."] });

        var parentFields = await _fieldRepo.ListByTableAsync(parent.Id, ct);
        var childFields = await _fieldRepo.ListByTableAsync(child.Id, ct);

        // Validate every summary spec up front — before any field is created — so a bad
        // function/target combination can't leave a half-built relationship behind. Function names
        // are normalized to their canonical casing here, and that normalized list is what's stored.
        var summaries = new List<CreateSummarySpec>(command.Summaries.Count);
        App? app = command.Summaries.Count > 0 ? await _appRepo.GetByIdAsync(parent.AppId, ct) : null;
        foreach (var spec in command.Summaries)
        {
            var function = SummaryFunctions.Normalize(spec.Function)
                ?? throw new ValidationException(new Dictionary<string, string[]> { ["summaries"] = [$"Unknown summary function '{spec.Function}'."] });
            var target = spec.TargetFid.HasValue ? childFields.FirstOrDefault(f => f.Fid == spec.TargetFid) : null;
            SummaryTargetValidator.Validate(function, spec.TargetFid, target, spec.TargetSubField);

            // An existing field reused as the reference keeps its own encryption flag; a brand-new
            // reference is encrypted exactly when the app is (covered by the app check).
            var encryptionProblem = command.ReferenceFieldFid is int refFid
                ? SummaryEncryptionGuard.FindProblem(app!, childFields, refFid, spec.TargetFid, null, null)
                : SummaryEncryptionGuard.FindProblem(app!, childFields, spec.TargetFid, null, null);
            if (encryptionProblem is not null)
                throw new ValidationException(new Dictionary<string, string[]> { ["summaries"] = [encryptionProblem] });

            summaries.Add(spec with { Function = function });
        }

        // Resolve the per-relationship display key: find the parent field matching DisplayKeyFieldFid.
        // Only uniqueness is required — any field type is eligible as a display key override (this is
        // a lighter-weight, per-relationship display choice, not the table-wide Set Key feature, so it
        // isn't restricted to Set Key's scalar-type allowlist).
        long? displayKeyFieldId = null;
        int? displayKeyFid = null;
        if (command.DisplayKeyFieldFid is int dkFid && dkFid != 3 /* 3 = Record ID# = standard */)
        {
            var dkField = parentFields.FirstOrDefault(f => f.Fid == dkFid)
                ?? throw new ValidationException(new Dictionary<string, string[]> { ["displayKeyFieldFid"] = [$"Field {dkFid} not found on the parent table."] });
            if (!dkField.IsUnique)
                throw new ValidationException(new Dictionary<string, string[]> { ["displayKeyFieldFid"] = ["The display key field must be unique across all parent records."] });
            displayKeyFieldId = dkField.Id;
            displayKeyFid = dkFid;
        }

        // 1. Reference field on the child (physical FK column). Settings get the relationship id after creation.
        //    Two paths: create a brand-new Reference field, or convert an existing child field in place.
        AppField refField;
        var referenceIsExistingField = command.ReferenceFieldFid is not null;
        if (!referenceIsExistingField)
        {
            refField = await _fieldFactory.CreateAsync(
                child, FieldTypeCodeNames.Reference, command.ReferenceFieldLabel!.Trim(),
                command.IsReferenceRequired,
                new ReferenceSettings { ParentTableId = parent.Id }, ct);
        }
        else
        {
            var existing = childFields.FirstOrDefault(f => f.Fid == command.ReferenceFieldFid!.Value)
                ?? throw new NotFoundException("Field", command.ReferenceFieldFid!.Value);

            // Only a plain, non-system field whose type suits the parent's key field may be repurposed as the
            // reference (see ReferenceFieldCompatibility). With no Set Key / alternate key the key is Record ID#.
            var keyField = KeyFieldResolver.ResolveDisplayKey(
                new Relationship { DisplayKeyFieldId = displayKeyFieldId }, parent, parentFields);
            var keyTypeCode = keyField is null || keyField.Fid == 3
                ? nameof(Domain.Enums.FieldTypeCode.Number)
                : keyField.TypeCode;
            if (existing.IsSystem || !existing.Fid.HasValue
                || !ReferenceFieldCompatibility.IsAllowed(keyTypeCode, existing.TypeCode))
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["referenceFieldFid"] = [$"The reference field must be an existing, non-system field on the child table whose type matches the parent's key field ({keyTypeCode})."],
                });

            var refType = await _fieldTypeRepo.GetByCodeAsync(FieldTypeCodeNames.Reference, ct)
                ?? throw new NotFoundException("FieldType", "Reference");

            // A Number field some Summary already sums/averages can't silently become a Reference —
            // checked before anything is written, so a refusal leaves no half-built relationship.
            await SummaryDependencyGuard.EnsureTypeChangeKeepsSummariesValidAsync(
                existing, FieldTypeCodeNames.Reference, _relRepo, _tableRepo, _fieldRepo, ct);

            // A plain Number field holding Record IDs is already numeric, so that conversion is metadata-only.
            // Every other type (text, currency, date, formula…) holds the parent's human key, so each row's value
            // is translated to the parent's Record ID first; nothing is written if any value has no match.
            var metadataOnly = existing.TypeCode == nameof(Domain.Enums.FieldTypeCode.Number)
                && keyTypeCode == nameof(Domain.Enums.FieldTypeCode.Number);
            if (!metadataOnly)
                await ConvertExistingFieldValuesAsync(child, childFields, existing, parent, keyField, refType, ct);

            await _fieldRepo.UpdateFieldTypeAsync(
                existing.Id, refType.Id,
                Serialize(new ReferenceSettings { ParentTableId = parent.Id }),
                command.IsReferenceRequired, ct);
            existing.FieldTypeId = refType.Id;
            existing.TypeCode = refType.Code;
            existing.IsRequired = command.IsReferenceRequired;
            existing.IsEncrypted = false;   // a Reference column is never encrypted
            refField = existing;
        }

        // 2. Relationship row.
        var (relId, relPublicId) = await _relRepo.CreateAsync(new Relationship
        {
            AppId = child.AppId,
            ParentTableId = parent.Id,
            ChildTableId = child.Id,
            ReferenceFieldId = refField.Id,
            ReferenceFid = refField.Fid!.Value,
            ProxyFieldId = null,
            ReferenceFieldIsExisting = referenceIsExistingField,
            DisplayKeyFieldId = displayKeyFieldId,
        }, ct);

        await _fieldRepo.UpdateSettingsAsync(refField.Id,
            Serialize(new ReferenceSettings { RelationshipId = relId, ParentTableId = parent.Id }), ct);

        // 3. Lookup fields on the child (first one becomes the proxy).
        var createdFields = new List<RelationshipFieldDto> { new(refField.PublicId, refField.Fid!.Value, refField.Label ?? refField.Name, "reference", "Reference") };
        // Same precedence as KeyFieldResolver.ResolveDisplayKey (the just-resolved override, else the
        // parent's global Set Key, else Record ID#) — matches what GetAsync returns on a later reload.
        var parentKeyField = KeyFieldResolver.ResolveDisplayKey(
            new Relationship { DisplayKeyFieldId = displayKeyFieldId }, parent, parentFields)
            ?? parentFields.FirstOrDefault(f => f.IsSystem && f.Fid == 3)
            ?? parentFields.FirstOrDefault(f => f.Fid == 3);
        if (parentKeyField is not null)
        {
            var keyTypeCode = parentKeyField.Fid == 3 ? "Record ID#" : parentKeyField.TypeCode;
            createdFields.Add(new(parentKeyField.PublicId, parentKeyField.Fid ?? 3, parentKeyField.Label ?? parentKeyField.Name, "key", keyTypeCode));
        }
        // Only auto-append the reference to forms when it's newly created; an existing field is likely already placed.
        var childAddFids = referenceIsExistingField ? new List<int>() : new List<int> { refField.Fid!.Value };
        AppField? firstLookup = null;
        foreach (var spec in command.Lookups)
        {
            var src = parentFields.FirstOrDefault(f => f.Fid == spec.SourceFid)
                ?? throw new NotFoundException("Field", spec.SourceFid);
            ValidateSubField(spec.SourceSubField, src.TypeCode, "sourceSubField");
            var sourceTypeCode = await LookupChain.ResolveForNewLookupAsync(src, _fieldRepo, ct);
            var lookup =await _fieldFactory.CreateAsync(child, FieldTypeCodeNames.Lookup, spec.Label.Trim(), false,
                new LookupSettings
                {
                    RelationshipId = relId,
                    ReferenceFid = refField.Fid!.Value,
                    SourceTableId = parent.Id,
                    SourceFid = spec.SourceFid,
                    SourceTypeCode = sourceTypeCode,
                    SourceSubField = spec.SourceSubField,
                }, ct);
            firstLookup ??= lookup;
            createdFields.Add(new(lookup.PublicId, lookup.Fid!.Value, lookup.Label ?? lookup.Name, "lookup", sourceTypeCode));
            childAddFids.Add(lookup.Fid!.Value);
        }

        if (firstLookup is not null)
            await _relRepo.UpdateProxyFieldAsync(relId, firstLookup.Id, ct);

        // 4. Summary fields on the parent.
        var parentAddFids = new List<int>();
        foreach (var spec in summaries)
        {
            var target = spec.TargetFid.HasValue ? childFields.FirstOrDefault(f => f.Fid == spec.TargetFid) : null;
            ValidateSubField(spec.TargetSubField, target?.TypeCode, "targetSubField");
            if (spec.TargetSubField is not null && spec.Function is SummaryFunctions.Sum or SummaryFunctions.Avg)
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["targetSubField"] = [$"'{spec.Function}' can't aggregate an address sub-field (always text); use Count, Exists, Min, or Max."],
                });
            var summary = await _fieldFactory.CreateAsync(parent, FieldTypeCodeNames.Summary, spec.Label.Trim(), false,
                new SummarySettings
                {
                    RelationshipId = relId,
                    ChildTableId = child.Id,
                    ReferenceFid = refField.Fid!.Value,
                    Function = spec.Function,
                    TargetFid = spec.TargetFid,
                    TargetTypeCode = target?.TypeCode,
                    TargetSubField = spec.TargetSubField,
                }, ct);
            createdFields.Add(new(summary.PublicId, summary.Fid!.Value, summary.Name, "summary"));
            parentAddFids.Add(summary.Fid!.Value);
        }

        // 5. Auto-create a Report Link on the parent: "See {child.Name}" — navigates to filtered child records.
        var appPublicId = await _appRepo.GetPublicIdByIdAsync(parent.AppId, ct);
        var reportLinkLabel = $"{child.Name} records";
        var reportLink = await _fieldFactory.CreateAsync(parent, FieldTypeCodeNames.ReportLink, reportLinkLabel, false,
            new ReportLinkSettings
            {
                RelationshipId = relId,
                TargetAppPublicId = appPublicId.ToString(),
                TargetTablePublicId = child.PublicId.ToString(),
                TargetFid = refField.Fid!.Value,
                SourceFid = null, // null = use Record ID# (Fid 3)
                LinkText = $"See related {child.Name}",
                OpenInNewWindow = false,
            }, ct);
        createdFields.Add(new(reportLink.PublicId, reportLink.Fid!.Value, reportLink.Label ?? reportLink.Name, "reportlink", reportLink.TypeCode));
        parentAddFids.Add(reportLink.Fid!.Value);

        // 6. Auto-append new fields to forms that opt in.
        await _fieldFactory.AppendToAutoAddFormsAsync(child.PublicId, childAddFids, ct);
        await _fieldFactory.AppendToAutoAddFormsAsync(parent.PublicId, parentAddFids, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.SchemaChanged, AuditEntityTypes.AppField, relId.ToString(),
            $"Relationship created: {child.Name} → {parent.Name}", appId: child.AppId, ct: ct);

        return new RelationshipDto
        {
            PublicId = relPublicId,
            ParentTablePublicId = parent.PublicId,
            ParentTableName = parent.Name,
            ChildTablePublicId = child.PublicId,
            ChildTableName = child.Name,
            ReferenceFid = refField.Fid!.Value,
            ReferenceFieldName = refField.Name,
            ProxyFid = firstLookup?.Fid,
            DisplayKeyFid = displayKeyFid,
            Fields = createdFields,
        };
    }

    /// <summary>
    /// Turns an existing child field that holds the parent's human key (a name, code, date…) into a column of
    /// parent Record IDs. Stored fields are read decrypted; a Formula field is evaluated per row (it has no
    /// column, so one is created). Every row's value is matched to a parent record through the key column;
    /// if any value has no match nothing is written.
    /// </summary>
    private async Task ConvertExistingFieldValuesAsync(
        AppTable child, IReadOnlyList<AppField> childFields, AppField existing,
        AppTable parent, AppField? keyField, FieldType refType, CancellationToken ct)
    {
        var isFormula = PhysicalNaming.IsComputedTypeCode(existing.TypeCode);
        var rows = await _recordRepo.ListAllRowsDecryptedAsync(child, childFields, ct);

        IReadOnlyList<IReadOnlyDictionary<long, object?>>? computed = null;
        if (isFormula)
        {
            var relational = await _relationalProjector.ProjectAsync(child, childFields, rows, ct);
            computed = _formulaProjector.Project(childFields, rows, relational, child);
        }

        var column = isFormula ? null : PhysicalNaming.GetPhysicalColumnName(existing);
        var rowTexts = new Dictionary<long, string>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (!rows[i].TryGetValue("Id", out var idVal) || idVal is null) continue;
            var value = isFormula ? computed![i].GetValueOrDefault(existing.Fid!.Value) : rows[i].GetValueOrDefault(column!);
            var text = KeyText(value, existing.TypeCode);
            if (!string.IsNullOrEmpty(text)) rowTexts[Convert.ToInt64(idVal)] = text;
        }

        var distinct = rowTexts.Values.Distinct(StringComparer.Ordinal).ToList();
        var typed = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var text in distinct)
            if (KeyFieldResolver.ConvertToColumnType(keyField, text) is { } v) typed[text] = v;

        var found = typed.Count == 0
            ? new Dictionary<object, long>()
            : (await _recordRepo.GetIdsByColumnValuesAsync(
                parent, KeyFieldResolver.ColumnName(keyField), typed.Values.ToList(), ct))
                .ToDictionary(p => p.Key, p => p.Value);

        var parentIdByText = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (text, value) in typed)
        {
            var hit = found.FirstOrDefault(p =>
                Equals(p.Key, value)
                || string.Equals(p.Key.ToString()?.Trim(), text, StringComparison.OrdinalIgnoreCase));
            if (hit.Key is not null) parentIdByText[text] = hit.Value;
        }

        var unmatched = distinct.Where(t => !parentIdByText.ContainsKey(t)).Take(5).ToList();
        if (unmatched.Count > 0)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["referenceFieldFid"] = [$"These values have no matching record in {parent.Name}: {string.Join(", ", unmatched.Select(u => $"\"{u}\""))}. Fix or clear them first."],
            });

        var parentIdByRow = rowTexts.ToDictionary(p => p.Key, p => parentIdByText[p.Value]);

        // The column the values land in. A Formula has none, so create it as a Reference (BIGINT) column.
        var target = existing;
        if (isFormula)
        {
            var physical = PhysicalNaming.ColumnName(existing.Fid!.Value);
            await _fieldRepo.UpdatePhysicalColumnNameAsync(existing.Id, physical, ct);
            existing.PhysicalColumnName = physical;
            target = new AppField
            {
                Id = existing.Id, Fid = existing.Fid, AppTableId = existing.AppTableId, Name = existing.Name,
                FieldTypeId = refType.Id, TypeCode = refType.Code, PhysicalColumnName = physical,
            };
            await _schemaEngine.AddColumnAsync(child, target, ct);
        }
        await _schemaEngine.ConvertColumnToReferenceAsync(child, target, parentIdByRow, ct);
    }

    /// <summary>A stored/computed value as the culture-independent text used to look up the parent key. A
    /// Text - Multiple Choice holding a single choice (a JSON array of one) is unwrapped to that choice.</summary>
    private static string? KeyText(object? value, string typeCode)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var text = (value switch
        {
            null => null,
            JsonElement je => je.ValueKind == JsonValueKind.String ? je.GetString() : je.GetRawText(),
            DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd", inv) : d.ToString("yyyy-MM-ddTHH:mm:ss", inv),
            DateOnly d => d.ToString("yyyy-MM-dd", inv),
            IFormattable f => f.ToString(null, inv),
            _ => value.ToString(),
        })?.Trim();

        if (typeCode == nameof(Domain.Enums.FieldTypeCode.MultiSelect) && text is { Length: > 0 } && text[0] == '[')
        {
            try
            {
                var items = JsonSerializer.Deserialize<string[]>(text);
                return items is { Length: 1 } ? items[0]?.Trim() : text;   // several choices stay as-is → no match → clear error
            }
            catch (JsonException) { }
        }
        return text;
    }

    /// <summary>A sub-field only makes sense against a composite Address field, and only for one
    /// of its real JSON keys — reject anything else eagerly with a clear message rather than
    /// silently creating a Lookup/Summary that will always resolve to null.</summary>
    private static void ValidateSubField(string? subField, string? fieldTypeCode, string paramName)
    {
        if (subField is null)
            return;

        if (fieldTypeCode != nameof(Domain.Enums.FieldTypeCode.Address))
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [paramName] = ["A sub-field can only target a composite Address field."],
            });

        if (!AddressSubFields.All.Contains(subField))
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [paramName] = [$"Must be one of: {string.Join(", ", AddressSubFields.All)}."],
            });
    }

    private static string Serialize(object settings) => JsonSerializer.Serialize(settings, JsonOpts);

    // Compile-time guard that the field-type code strings match the enum.
    private static class FieldTypeCodeNames
    {
        public const string Reference = nameof(Domain.Enums.FieldTypeCode.Reference);
        public const string Lookup = nameof(Domain.Enums.FieldTypeCode.Lookup);
        public const string Summary = nameof(Domain.Enums.FieldTypeCode.Summary);
        public const string ReportLink = nameof(Domain.Enums.FieldTypeCode.ReportLink);
    }
}
