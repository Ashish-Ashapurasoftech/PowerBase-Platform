using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Records.Queries.ListRecords;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Relationships.Queries;

/// <summary>
/// Returns the child records that are linked to a given parent record through a specific
/// relationship. Used by the embedded "ChildRecords" form element to populate the inline
/// child grid on the parent's Add / Edit / View form.
///
/// Resolution chain:
///   1. Load the <see cref="Domain.Entities.Relationship"/> by its public id to get the
///      <see cref="Domain.Entities.Relationship.ReferenceFid"/> (the FK column on the child).
///   2. Load the parent table and resolve the parent's internal row id from its public id.
///   3. Delegate to <see cref="ListRecordsQueryHandler"/> with filterFid = ReferenceFid and
///      filterValue = parent's row id — exactly the same filter the Report-Link "related"
///      view already uses, so permissions, role filters, and projection are all applied
///      consistently.
/// </summary>
public class GetChildRecordsForParentQueryHandler
{
    private readonly IRelationshipRepository _relRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly ListRecordsQueryHandler _listHandler;

    public GetChildRecordsForParentQueryHandler(
        IRelationshipRepository relRepo,
        IAppTableRepository tableRepo,
        IRecordRepository recordRepo,
        ListRecordsQueryHandler listHandler)
    {
        _relRepo = relRepo;
        _tableRepo = tableRepo;
        _recordRepo = recordRepo;
        _listHandler = listHandler;
    }

    /// <summary>
    /// Fetches child records for the given parent record.
    /// </summary>
    /// <param name="relationshipPublicId">The relationship whose child table to query.</param>
    /// <param name="parentRecordPublicId">The public id of the parent record.</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Number of rows per page (capped at 200 by the list handler).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<PagedRecordResult> HandleAsync(
        Guid relationshipPublicId,
        string parentRecordIdString,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        // 1. Load the relationship to get the reference field's Fid (FK column on child) and
        //    the child table public id for routing to the list handler.
        var rel = await _relRepo.GetByPublicIdAsync(relationshipPublicId, ct)
            ?? throw new NotFoundException("Relationship", relationshipPublicId);

        // 2. Resolve the parent table and the parent record's internal numeric row id.
        //    The reference column stores the parent's plain long Id, not its PublicId.
        var parentTable = await _tableRepo.GetByIdAsync(rel.ParentTableId, ct);
        if (parentTable == null)
            throw new NotFoundException("Table", rel.ParentTableId);

        var childTable = await _tableRepo.GetByIdAsync(rel.ChildTableId, ct);
        if (childTable == null)
            throw new NotFoundException("Table", rel.ChildTableId);

        long parentRowId = 0;
        if (long.TryParse(parentRecordIdString, out var numericId) && numericId > 0)
        {
            parentRowId = numericId;
        }
        else if (Guid.TryParse(parentRecordIdString, out var parentRecordPublicId))
        {
            try
            {
                parentRowId = await _recordRepo.GetRecordIdByPublicIdAsync(parentTable, parentRecordPublicId, ct: ct);
            }
            catch
            {
                return new PagedRecordResult { Items = [], TotalCount = 0, Page = page, PageSize = pageSize };
            }
        }

        if (parentRowId <= 0)
        {
            return new PagedRecordResult { Items = [], TotalCount = 0, Page = page, PageSize = pageSize };
        }

        // 3. Delegate entirely to the existing list handler so that role-based visibility,
        //    view filters, formula projection, and relational projection are all applied
        //    identically to a normal record list.
        //    filterFid   = ReferenceFid           → the FK column on the child table.
        //    filterValue = parent's row id string → matches the stored bigint value.
        return await _listHandler.HandleAsync(
            new ListRecordsQuery(
                TablePublicId: childTable.PublicId,
                Page: page,
                PageSize: pageSize,
                FilterFid: rel.ReferenceFid,
                FilterValue: parentRowId.ToString()),
            ct);
    }

    public Task<PagedRecordResult> HandleAsync(
        Guid relationshipPublicId,
        Guid parentRecordPublicId,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
        => HandleAsync(relationshipPublicId, parentRecordPublicId.ToString(), page, pageSize, ct);
}
