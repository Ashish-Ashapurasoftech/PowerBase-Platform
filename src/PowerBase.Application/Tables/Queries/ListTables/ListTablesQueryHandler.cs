using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;

namespace PowerBase.Application.Tables.Queries.ListTables;

public class ListTablesResult
{
    public IReadOnlyList<AppTableListItemDto> Items { get; init; } = Array.Empty<AppTableListItemDto>();
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
}

public class ListTablesQueryHandler
{
    private static readonly HashSet<string> AllowedSortFields =
        new(StringComparer.OrdinalIgnoreCase) { "name", "singularLabel", "recordCount", "fieldCount", "createdOn" };

    private readonly IAppRepository _appRepo;
    private readonly IAppTableRepository _tableRepo;

    public ListTablesQueryHandler(IAppRepository appRepo, IAppTableRepository tableRepo)
    {
        _appRepo = appRepo;
        _tableRepo = tableRepo;
    }

    public async Task<ListTablesResult> HandleAsync(ListTablesQuery query, CancellationToken ct = default)
    {
        var app = await _appRepo.GetByPublicIdAsync(query.AppPublicId, ct);

        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize is < 1 or > 100 ? 20 : query.PageSize;
        var sortBy = AllowedSortFields.Contains(query.SortBy) ? query.SortBy : "name";

        var items = await _tableRepo.ListByAppPagedAsync(app.Id, page, pageSize, query.Search, sortBy, query.SortDesc, query.IsShowInBar, ct);
        var total = await _tableRepo.CountByAppAsync(app.Id, query.Search, query.IsShowInBar, ct);

        return new ListTablesResult { Items = items, Total = total, Page = page, PageSize = pageSize };
    }
}
