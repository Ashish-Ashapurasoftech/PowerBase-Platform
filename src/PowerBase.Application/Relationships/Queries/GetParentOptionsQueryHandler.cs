using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Relationships.Queries;

/// <summary>Lists selectable parent records for a Reference field picker, labelled by the
/// parent table's display field (or its first non-system field).</summary>
public class GetParentOptionsQueryHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRelationshipRepository _relRepo;
    private readonly IRecordRepository _recordRepo;

    public GetParentOptionsQueryHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRelationshipRepository relRepo,
        IRecordRepository recordRepo)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _relRepo = relRepo;
        _recordRepo = recordRepo;
    }

    public async Task<GetParentOptionsResult> HandleAsync(
        Guid relationshipPublicId, string? search, int take, CancellationToken ct = default)
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
        var options = await _recordRepo.SearchForReferenceAsync(
            parent, labelFields, search, take == 0 ? 50 : take, primaryLabelField, ct);

        return new GetParentOptionsResult(headers, options);
    }

    private static IReadOnlyList<AppField> ResolveLabelFields(AppTable parent, IReadOnlyList<AppField> parentFields)
    {
        var fields = new List<AppField>();

        if (parent.DefaultRecordPickerField1Id.HasValue)
        {
            var f1 = parentFields.FirstOrDefault(f => f.Id == parent.DefaultRecordPickerField1Id.Value);
            if (f1 != null) fields.Add(f1);

            if (parent.DefaultRecordPickerField2Id.HasValue)
            {
                var f2 = parentFields.FirstOrDefault(f => f.Id == parent.DefaultRecordPickerField2Id.Value);
                if (f2 != null) fields.Add(f2);
            }

            if (parent.DefaultRecordPickerField3Id.HasValue)
            {
                var f3 = parentFields.FirstOrDefault(f => f.Id == parent.DefaultRecordPickerField3Id.Value);
                if (f3 != null) fields.Add(f3);
            }

            if (fields.Count > 0) return fields;
        }

        if (parent.DisplayFieldId.HasValue)
        {
            var display = parentFields.FirstOrDefault(f => f.Id == parent.DisplayFieldId.Value);
            if (display is not null) return [display];
        }
        // Fall back to the first non-system, non-computed field with a physical column.
        var fallback = parentFields.FirstOrDefault(f => !f.IsSystem && f.Fid.HasValue
            && !Domain.Constants.PhysicalNaming.IsComputedTypeCode(f.TypeCode));
        if (fallback != null) return [fallback];

        // Ultimate fallback: Record ID# (Fid 3), so a table with zero business fields still shows
        // something in the picker instead of blank headers/options.
        var recordId = parentFields.FirstOrDefault(f => f.IsSystem && f.Fid == 3)
            ?? parentFields.FirstOrDefault(f => f.IsSystem);
        return recordId != null ? [recordId] : [];
    }
}
