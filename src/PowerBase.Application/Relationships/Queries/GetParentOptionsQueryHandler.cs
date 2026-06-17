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

    public async Task<IReadOnlyList<ReferenceOption>> HandleAsync(
        Guid relationshipPublicId, string? search, int take, CancellationToken ct = default)
    {
        var rel = await _relRepo.GetByPublicIdAsync(relationshipPublicId, ct)
            ?? throw new NotFoundException("Relationship", relationshipPublicId);

        var parent = await _tableRepo.GetByIdAsync(rel.ParentTableId, ct);
        var parentFields = await _fieldRepo.ListByTableAsync(parent.Id, ct);

        var labelField = ResolveLabelField(parent, parentFields);
        return await _recordRepo.SearchForReferenceAsync(parent, labelField, search, take == 0 ? 50 : take, ct);
    }

    private static AppField? ResolveLabelField(AppTable parent, IReadOnlyList<AppField> parentFields)
    {
        if (parent.DisplayFieldId.HasValue)
        {
            var display = parentFields.FirstOrDefault(f => f.Id == parent.DisplayFieldId.Value);
            if (display is not null) return display;
        }
        // Fall back to the first non-system, non-computed field with a physical column.
        return parentFields.FirstOrDefault(f => !f.IsSystem && f.Fid.HasValue
            && !Domain.Constants.PhysicalNaming.IsComputedTypeCode(f.TypeCode));
    }
}
