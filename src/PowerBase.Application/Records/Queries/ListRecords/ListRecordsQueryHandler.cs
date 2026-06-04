using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Records.Queries.ListRecords;

public class PagedRecordResult
{
    public IReadOnlyList<Records.RecordResult> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
}

public class ListRecordsQueryHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IRolePermissionEnforcer _enforcer;

    public ListRecordsQueryHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IRolePermissionEnforcer enforcer)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _enforcer = enforcer;
    }

    public async Task<PagedRecordResult> HandleAsync(ListRecordsQuery query, CancellationToken ct = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        var table = await _tableRepo.GetByPublicIdAsync(query.TablePublicId, ct);
        var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);

        var access = await _enforcer.GetTableAccessAsync(table, fields, ct);
        if (!access.CanView)
            return new PagedRecordResult { Items = [], TotalCount = 0, Page = page, PageSize = pageSize };

        var visibleFields = access.VisibleFields;
        var rows = await _recordRepo.ListAsync(
            table, visibleFields, page, pageSize,
            filterTree: access.ViewFilter, restrictToCreatedBy: access.RestrictToCreatedBy, ct: ct);
        var total = await _recordRepo.CountAsync(
            table, filterTree: access.ViewFilter, restrictToCreatedBy: access.RestrictToCreatedBy, ct: ct);

        return new PagedRecordResult
        {
            Items = rows.Select(r => Records.RecordResult.FromRow(r, visibleFields)).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize,
        };
    }
}
