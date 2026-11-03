using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Records.Queries.GetRecord;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Relationships.Queries;

/// <summary>Where a child record's parent can be opened: the parent table and record PublicIds.</summary>
public sealed record ParentRecordLink(Guid ParentTablePublicId, Guid ParentRecordPublicId);

/// <summary>
/// Resolves the parent record a child record's Reference points at, so a Reference or Lookup set to "Display as a link
/// to the parent record" (Quickbase's DisplayAsLinkToParent) can open it. Resolved on click rather than on every read. Both records
/// go through <see cref="GetRecordQueryHandler"/>, the same role checks (view scope, view filter, computed-field
/// rules) as opening them directly, so the link never reveals a record the user can't open.
/// </summary>
public class GetParentRecordQueryHandler
{
    private readonly IRelationshipRepository _relRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IRolePermissionEnforcer _enforcer;
    private readonly GetRecordQueryHandler _getRecord;

    public GetParentRecordQueryHandler(
        IRelationshipRepository relRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IRolePermissionEnforcer enforcer,
        GetRecordQueryHandler getRecord)
    {
        _relRepo = relRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _enforcer = enforcer;
        _getRecord = getRecord;
    }

    /// <param name="childTablePublicId">The table in the route; must be the relationship's child table.</param>
    /// <returns>Null when the record has no parent, or the user can't see the Reference or open the parent.</returns>
    public async Task<ParentRecordLink?> HandleAsync(
        Guid childTablePublicId, Guid relationshipPublicId, Guid childRecordPublicId, CancellationToken ct = default)
    {
        var rel = await _relRepo.GetByPublicIdAsync(relationshipPublicId, ct)
            ?? throw new NotFoundException("Relationship", relationshipPublicId);
        var child = await _tableRepo.GetByIdAsync(rel.ChildTableId, ct);
        if (child.PublicId != childTablePublicId)
            throw new NotFoundException("Relationship", relationshipPublicId);

        // The child record must be one this user can open (throws NotFound otherwise), with the Reference visible.
        await _getRecord.HandleAsync(new GetRecordQuery(child.PublicId, childRecordPublicId), ct);
        var childFields = await _fieldRepo.ListByTableAsync(child.Id, ct);
        var access = await _enforcer.GetTableAccessAsync(child, childFields, ct);
        var reference = access.VisibleFields.FirstOrDefault(f => f.Id == rel.ReferenceFieldId);
        if (reference is null) return null;

        // The Reference column always stores the parent row Id, whatever key it displays.
        var row = await _recordRepo.GetByPublicIdAsync(child, [reference], childRecordPublicId, ct: ct);
        if (!row.TryGetValue(PhysicalNaming.GetPhysicalColumnName(reference), out var raw) || raw is null) return null;
        var parentRowId = Convert.ToInt64(raw);

        var parent = await _tableRepo.GetByIdAsync(rel.ParentTableId, ct);
        var parentRows = await _recordRepo.GetRowsByIdsAsync(parent, [], [parentRowId], ct);
        if (!parentRows.TryGetValue(parentRowId, out var parentRow)
            || !parentRow.TryGetValue("PublicId", out var parentPublicIdRaw) || parentPublicIdRaw is not Guid parentPublicId)
            return null;

        try
        {
            await _getRecord.HandleAsync(new GetRecordQuery(parent.PublicId, parentPublicId), ct);
        }
        catch (NotFoundException)
        {
            return null; // the parent exists but this user can't open it
        }

        return new ParentRecordLink(parent.PublicId, parentPublicId);
    }
}
