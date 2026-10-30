using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.FieldReferences;
using PowerBase.Application.Fields;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Relationships;

/// <summary>
/// Provisions a relationship field (Reference/Lookup/Summary/Report Link/"Add" Formula URL) on a table — the shared
/// field-creation path used when growing a relationship (Add Lookup / Add Summary).
/// Mirrors the inline logic in <c>CreateRelationshipCommandHandler</c>: resolves the
/// field type, allocates a Fid, creates the meta row, and adds a physical column only
/// for non-computed (Reference) fields. Also appends new fields to auto-add forms.
/// </summary>
public sealed class RelationshipFieldFactory
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The auto "Add {child}" link's type — a Formula_{X} variant, so no FieldTypeCode enum member.</summary>
    public const string FormulaUrlTypeCode = "Formula_Url";

    private readonly IAppFieldRepository _fieldRepo;
    private readonly IFieldTypeRepository _fieldTypeRepo;
    private readonly ISchemaEngineService _schemaEngine;
    private readonly IFormRepository _formRepo;
    private readonly IQueryContext _queryContext;
    private readonly IFieldNameResolver _fieldNameResolver;
    private readonly IFieldReferenceIndexer _refIndexer;

    public RelationshipFieldFactory(
        IAppFieldRepository fieldRepo,
        IFieldTypeRepository fieldTypeRepo,
        ISchemaEngineService schemaEngine,
        IFormRepository formRepo,
        IQueryContext queryContext,
        IFieldNameResolver fieldNameResolver,
        IFieldReferenceIndexer? refIndexer = null)
    {
        _refIndexer = refIndexer ?? NullFieldReferenceIndexer.Instance;
        _fieldRepo = fieldRepo;
        _fieldTypeRepo = fieldTypeRepo;
        _schemaEngine = schemaEngine;
        _formRepo = formRepo;
        _queryContext = queryContext;
        _fieldNameResolver = fieldNameResolver;
    }

    /// <param name="isEncrypted">Marks the field's column as encrypted (needed when its values are copied raw from an encrypted column).</param>
    /// <param name="explicitFid">Import only: the Fid to keep (a source field's number). Used when it is free and not reserved; otherwise the next free one.</param>
    /// <param name="hidden">Sets <c>IsHidden</c> (kept out of field lists) and keeps the field out of searches, sorts, filters and reports (it is also never auto-added to forms by this method).</param>
    public async Task<AppField> CreateAsync(
        AppTable table, string typeCode, string label, bool isRequired, object settingsObj, CancellationToken ct,
        bool isEncrypted = false, bool hidden = false, int? explicitFid = null)
    {
        var fieldType = await _fieldTypeRepo.GetByCodeAsync(typeCode, ct) ?? throw new NotFoundException("FieldType", typeCode);
        var requestedLabel = label;

        if (typeCode == nameof(Domain.Enums.FieldTypeCode.Lookup)
            || typeCode == nameof(Domain.Enums.FieldTypeCode.Reference)
            || typeCode == nameof(Domain.Enums.FieldTypeCode.ReportLink)
            || typeCode == FormulaUrlTypeCode
            || hidden)   // a hidden helper field must never fail on a label clash (e.g. an earlier "(original)" left by a deleted relationship)
        {
            label = await ResolveUniqueLabelAsync(table.Id, label, ct);
        }
        else if (await _fieldRepo.LabelExistsInTableAsync(table.Id, label, ct: ct))
        {
            throw new DuplicateException("Field", "label", label);
        }

        var name = await _fieldNameResolver.GenerateUniqueNameAsync(table.Id, label, isSystem: false, ct);

        // The "Add {child}" link starts with the Advanced settings a hand-added Formula URL gets — not
        // searchable/sortable/filterable, so a quick search for "app" doesn't match every record's URL.
        var defaults = typeCode == FormulaUrlTypeCode ? FieldAdvancedSettingsCapability.Resolve(typeCode, null) : null;

        var fid = await PowerBase.Application.Fields.FieldFidPolicy.ResolveAsync(
            _fieldRepo, table.Id, requestedLabel, explicitFid, ct,
            useExpected: typeCode is nameof(Domain.Enums.FieldTypeCode.Lookup) or nameof(Domain.Enums.FieldTypeCode.Reference) or nameof(Domain.Enums.FieldTypeCode.Summary));
        var field = new AppField
        {
            AppTableId = table.Id,
            FieldTypeId = fieldType.Id,
            TypeCode = fieldType.Code,
            Name = name,
            Label = label,
            IsRequired = isRequired,
            Fid = fid,
            Settings = Serialize(settingsObj),
            CreatedBy = _queryContext.UserId,
            IsSearchable = !hidden && (defaults?.Searchable ?? true),
            IsSortable = !hidden && (defaults?.Sortable ?? true),
            IsFilterable = !hidden && (defaults?.Filterable ?? true),
            IsReportable = !hidden && (defaults?.Reportable ?? true),
            IsEncrypted = isEncrypted,
            IsHidden = hidden,
        };

        var (id, publicId) = await _fieldRepo.CreateAsync(field, ct);
        field.Id = id;
        field.PublicId = publicId;

        if (!PhysicalNaming.IsComputedTypeCode(field.TypeCode))
        {
            var col = PhysicalNaming.ColumnName(fid);
            await _fieldRepo.UpdatePhysicalColumnNameAsync(id, col, ct);
            field.PhysicalColumnName = col;
            await _schemaEngine.AddColumnAsync(table, field, ct);
        }

        // A Lookup/Summary/Report Link points at fields of other tables — index what it reads.
        await _refIndexer.ReindexFieldAsync(publicId, ct);
        return field;
    }

    public async Task AppendToAutoAddFormsAsync(Guid tablePublicId, IReadOnlyList<int> fids, CancellationToken ct)
    {
        if (fids.Count == 0) return;
        var forms = await _formRepo.ListByTableAsync(tablePublicId, ct);
        foreach (var form in forms.Where(f => f.AutoAddNewFields && !f.IsQuickPeekForm))
        {
            foreach (var fid in fids)
                await _formRepo.AppendFieldToLastSectionAsync(form.Id, fid, ct);
            await _refIndexer.ReindexFormAsync(form.PublicId, ct);
        }
    }

    private async Task<string> ResolveUniqueLabelAsync(long tableId, string label, CancellationToken ct)
    {
        if (!await _fieldRepo.LabelExistsInTableAsync(tableId, label, ct: ct))
            return label;

        var baseLabel = label;
        var suffix = 1;

        var match = System.Text.RegularExpressions.Regex.Match(label, @"^(.*?)\s+(\d+)$");
        if (match.Success)
        {
            baseLabel = match.Groups[1].Value;
            if (int.TryParse(match.Groups[2].Value, out var n))
                suffix = n + 1;
        }

        string candidate;
        do
        {
            candidate = $"{baseLabel} {suffix}";
            suffix++;
        } while (await _fieldRepo.LabelExistsInTableAsync(tableId, candidate, ct: ct));

        return candidate;
    }

    public static string Serialize(object settings) => JsonSerializer.Serialize(settings, JsonOpts);
}
