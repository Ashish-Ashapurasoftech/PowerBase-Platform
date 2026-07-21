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

        var labelFields = ResolveLabelFields(parent, parentFields);
        var headers = labelFields.Select(f => f.Name).ToList();
        var options = await _recordRepo.SearchForReferenceAsync(parent, labelFields, search, take == 0 ? 50 : take, ct);

        // Default key: SearchForReferenceAsync already returns the row Id as text — nothing to do.
        // Custom key: swap each option's Id (currently the row Id) for the table's key-field value,
        // since that's what actually gets stored in the child's reference column.
        var keyField = await KeyFieldResolver.ResolveAsync(parent, _fieldRepo, ct);
        if (keyField is not null && options.Count > 0)
        {
            var rowIds = options.Select(o => long.Parse(o.Id)).ToList();
            var col = KeyFieldResolver.ColumnName(keyField);
            var keyValues = await _recordRepo.GetColumnValuesByIdsAsync(parent, col, rowIds, ct);
            options = options
                .Select(o => keyValues.TryGetValue(long.Parse(o.Id), out var kv)
                    ? new ReferenceOption { Id = KeyFieldResolver.FormatForSubmit(kv), Value1 = o.Value1, Value2 = o.Value2, Value3 = o.Value3 }
                    : o)
                .Where(o => !string.IsNullOrEmpty(o.Id)) // drop rows whose key value is blank — unusable as a reference
                .ToList();
        }

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
            
        return fallback != null ? [fallback] : [];
    }
}
