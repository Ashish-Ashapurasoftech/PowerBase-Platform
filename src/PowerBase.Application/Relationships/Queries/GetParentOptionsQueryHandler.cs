using System.Globalization;
using PowerBase.Application.Common.Formatting;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.ValueObjects;

namespace PowerBase.Application.Relationships.Queries;

/// <summary>Lists selectable parent records for a Reference field picker, labelled by the
/// parent table's display field (or its first non-system field).</summary>
public class GetParentOptionsQueryHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRelationshipRepository _relRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IRelationalProjector _relationalProjector;
    private readonly IFormulaProjector _formulaProjector;
    private readonly IQueryContext _queryContext;

    public GetParentOptionsQueryHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRelationshipRepository relRepo,
        IRecordRepository recordRepo,
        IRelationalProjector relationalProjector,
        IFormulaProjector formulaProjector,
        IQueryContext queryContext)
    {
        _queryContext = queryContext;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _relRepo = relRepo;
        _recordRepo = recordRepo;
        _relationalProjector = relationalProjector;
        _formulaProjector = formulaProjector;
    }

    public async Task<GetParentOptionsResult> HandleAsync(
        Guid relationshipPublicId, string? search, int take,
        IReadOnlyDictionary<int, string?>? filterValues = null, CancellationToken ct = default)
    {
        var rel = await _relRepo.GetByPublicIdAsync(relationshipPublicId, ct)
            ?? throw new NotFoundException("Relationship", relationshipPublicId);

        var parent = await _tableRepo.GetByIdAsync(rel.ParentTableId, ct);
        var parentFields = await _fieldRepo.ListByTableAsync(parent.Id, ct);

        // The picker column shows the relationship's display key so it matches what the grid,
        // record view, and filter show (see KeyFieldResolver.ResolveDisplayKey): per-relationship
        // DisplayKeyFieldId override → parent table KeyFieldId → the table's configured picker
        // fields (Record ID# tables keep the friendly multi-column label instead of raw ids).
        //
        // When an override/virtual key is used (displayKey is not null), we show:
        //   column 1 = the key field itself (e.g. StatusCode: "C", "IP", "P")
        //   column 2+ = the table's configured descriptive picker fields (e.g. StatusName: "Completed", "In Progress")
        // This lets users see BOTH the key code AND a human-readable description so they can make
        // an informed selection.  For the standard Record ID# key, we fall back to ResolveLabelFields
        // which already returns descriptive picker fields (no raw Id column cluttering the UI).
        var displayKey = KeyFieldResolver.ResolveDisplayKey(rel, parent, parentFields);
        IReadOnlyList<AppField> labelFields;
        AppField? primaryLabelField = null;
        if (displayKey is not null)
        {
            // Start with the key field, then append descriptive picker fields (excluding the key
            // itself to avoid duplication). Cap at 3 total — SearchForReferenceAsync only
            // projects Value1, Value2, Value3.
            var descriptiveFields = ResolveLabelFields(parent, parentFields)
                .Where(f => f.Id != displayKey.Id)
                .Take(2)   // key takes slot 1, so descriptive can fill at most slots 2 & 3
                .ToList();
            var combined = new List<AppField> { displayKey };
            combined.AddRange(descriptiveFields);
            labelFields = combined;

            // The closed-input text (ReferenceOption.Label) always reads like the standard Record
            // ID# key does: the parent's descriptive/picker field, never the raw alternate key value
            // (e.g. a phone number). The key field still shows as column 1 in the multi-column list
            // above so users can see both while picking; falls back to the key field itself only if
            // the parent has no separate descriptive field configured.
            primaryLabelField = descriptiveFields.FirstOrDefault() ?? displayKey;
        }
        else
        {
            labelFields = ResolveLabelFields(parent, parentFields);
        }

        var headers = labelFields.Select(f => f.Label ?? f.Name).ToList();

        // The reference column always stores the parent row Id, so the picker always submits it
        // (option.Id). SearchForReferenceAsync already returns the row Id as text; DisplayKeyFieldId
        // only changes which column is shown as the label (labelFields above).
        var effectiveTake = take == 0 ? 50 : take;

        // Dependent dropdown: narrow the parent records by the reference field's filter conditions
        // against the form's current values (see ReferenceSettings.FilterConditions).
        var childFields = await _fieldRepo.ListByTableAsync(rel.ChildTableId, ct);
        var referenceField = childFields.FirstOrDefault(f => f.Id == rel.ReferenceFieldId);
        var filters = await ReferenceFilterResolver.BuildAsync(
            FormulaTypeMap.ParseReferenceSettings(referenceField?.Settings), childFields, parentFields,
            filterValues ?? new Dictionary<int, string?>(), _tableRepo, _fieldRepo, ct, _queryContext.UserId);
        var hasComputed = labelFields.Any(IsComputedLabel) || (primaryLabelField is not null && IsComputedLabel(primaryLabelField));
        if (!hasComputed)
        {
            var plain = await _recordRepo.SearchForReferenceAsync(
                parent, labelFields, search, effectiveTake, primaryLabelField, ct, filters);
            return new GetParentOptionsResult(headers, plain);
        }

        // A computed label (Formula/Lookup/Summary) has no SQL column, so it can't be searched in
        // SQL: fetch the (capped) page unfiltered, compute the labels, then filter in memory.
        var hasSearch = !string.IsNullOrWhiteSpace(search);
        var options = await _recordRepo.SearchForReferenceAsync(
            parent, labelFields, null, hasSearch ? 200 : effectiveTake, primaryLabelField, ct, filters);
        await FillComputedLabelsAsync(parent, parentFields, labelFields, primaryLabelField, options, ct);

        if (hasSearch)
        {
            options = options
                .Where(o => new[] { o.Value1, o.Value2, o.Value3 }
                    .Any(v => v is not null && v.Contains(search!, StringComparison.OrdinalIgnoreCase)))
                .Take(effectiveTake)
                .ToList();
        }

        return new GetParentOptionsResult(headers, options);
    }

    /// <summary>
    /// Computes the values of computed label fields (Formula/Lookup/Summary) for the fetched
    /// options — the same relational → formula projection every record read uses — and writes
    /// them into Value1..3 / Label. The SQL query only selects NULL placeholders for these.
    /// </summary>
    private async Task FillComputedLabelsAsync(
        AppTable parent, IReadOnlyList<AppField> parentFields, IReadOnlyList<AppField> labelFields,
        AppField? primaryLabelField, IReadOnlyList<ReferenceOption> options, CancellationToken ct)
    {
        if (options.Count == 0) return;

        var ids = options.Select(o => long.TryParse(o.Id, out var id) ? id : (long?)null)
            .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        var rowsById = await _recordRepo.GetRowsByIdsAsync(parent, parentFields, ids, ct);

        var pairs = options
            .Select(o => (Option: o, Row: long.TryParse(o.Id, out var id) && rowsById.TryGetValue(id, out var row) ? row : null))
            .Where(x => x.Row is not null)
            .ToList();
        if (pairs.Count == 0) return;

        var rows = pairs.Select(x => x.Row!).ToList();
        var relational = await _relationalProjector.ProjectAsync(parent, parentFields, rows, ct);
        var computed = _formulaProjector.Project(parentFields, rows, relational, parent);
        var appFormatting = new AppFormattingSettings();

        string? Compute(AppField field, IReadOnlyDictionary<long, object?> values)
        {
            var typeCode = field.TypeCode.StartsWith("Formula_", StringComparison.Ordinal)
                ? field.TypeCode["Formula_".Length..] switch { "Bool" => "Boolean", var t => t }
                : "Text";
            return values.TryGetValue(field.Fid!.Value, out var raw) && raw is not null
                ? DisplayValueFormatter.Format(raw, typeCode, field.Settings, appFormatting)
                : null;
        }

        for (var i = 0; i < pairs.Count; i++)
        {
            var (opt, _) = pairs[i];
            var values = computed[i];
            if (labelFields.Count > 0 && IsComputedLabel(labelFields[0])) opt.Value1 = Compute(labelFields[0], values);
            if (labelFields.Count > 1 && IsComputedLabel(labelFields[1])) opt.Value2 = Compute(labelFields[1], values);
            if (labelFields.Count > 2 && IsComputedLabel(labelFields[2])) opt.Value3 = Compute(labelFields[2], values);
            if (primaryLabelField is not null && IsComputedLabel(primaryLabelField)) opt.Label = Compute(primaryLabelField, values);
            else if (primaryLabelField is null && labelFields.Count > 0 && IsComputedLabel(labelFields[0])) opt.Label = opt.Value1;
        }
    }

    /// <summary>True for a label field whose value is computed at read time (Formula variants,
    /// Lookup, Summary) instead of being stored in a physical SQL column.</summary>
    private static bool IsComputedLabel(AppField f)
        => f.Fid.HasValue && Domain.Constants.PhysicalNaming.IsComputedTypeCode(f.TypeCode);

    /// <summary>
    /// Returns true when <paramref name="f"/> can serve as a picker label: it either has a
    /// physical storage column (projected directly in SQL), or it is a Formula / Lookup / Summary
    /// field computed on read (<see cref="FillComputedLabelsAsync"/>). ReportLink and ActionButton
    /// are computed too but have no meaningful text value, so they never serve as a label.
    /// </summary>
    private static bool CanServeAsLabel(AppField f)
        => f.Fid.HasValue && (!Domain.Constants.PhysicalNaming.IsComputedTypeCode(f.TypeCode)
            || f.TypeCode is "Lookup" or "Summary"
            || FormulaTypeMap.IsFormulaComputed(f.TypeCode, f.Settings));

    private static IReadOnlyList<AppField> ResolveLabelFields(AppTable parent, IReadOnlyList<AppField> parentFields)
    {
        var fields = new List<AppField>();

        if (parent.DefaultRecordPickerField1Id.HasValue)
        {
            // Skip fields that cannot serve as a label (ReportLink/ActionButton have no text value).
            // Formula/Lookup/Summary are allowed: their values are computed after the SQL query
            // (FillComputedLabelsAsync). The resolution falls through to the next tier otherwise.
            var f1 = parentFields.FirstOrDefault(f => f.Id == parent.DefaultRecordPickerField1Id.Value && CanServeAsLabel(f));
            if (f1 != null) fields.Add(f1);

            if (parent.DefaultRecordPickerField2Id.HasValue)
            {
                var f2 = parentFields.FirstOrDefault(f => f.Id == parent.DefaultRecordPickerField2Id.Value && CanServeAsLabel(f));
                if (f2 != null) fields.Add(f2);
            }

            if (parent.DefaultRecordPickerField3Id.HasValue)
            {
                var f3 = parentFields.FirstOrDefault(f => f.Id == parent.DefaultRecordPickerField3Id.Value && CanServeAsLabel(f));
                if (f3 != null) fields.Add(f3);
            }

            if (fields.Count > 0) return fields;
        }

        if (parent.DisplayFieldId.HasValue)
        {
            // Same guard: skip if the configured display field cannot serve as a label.
            var display = parentFields.FirstOrDefault(f => f.Id == parent.DisplayFieldId.Value && CanServeAsLabel(f));
            if (display is not null) return [display];
        }

        // Fall back to the first non-system field that can serve as a label.
        var fallback = parentFields.FirstOrDefault(f => !f.IsSystem && CanServeAsLabel(f));
        if (fallback != null) return [fallback];

        // Ultimate fallback: Record ID# (Fid 3), so a table with zero business fields still shows
        // something in the picker instead of blank headers/options.
        var recordId = parentFields.FirstOrDefault(f => f.IsSystem && f.Fid == 3)
            ?? parentFields.FirstOrDefault(f => f.IsSystem);
        return recordId != null ? [recordId] : [];
    }
}
