using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Apps.Queries.ListApps;

public class ListAppsResult
{
    public IReadOnlyList<AppListItemDto> Items { get; init; } = Array.Empty<AppListItemDto>();
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
}

public class ListAppsQueryHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IQueryContext _queryContext;

    public ListAppsQueryHandler(IAppRepository appRepo, IQueryContext queryContext)
    {
        _appRepo = appRepo;
        _queryContext = queryContext;
    }

    public async Task<ListAppsResult> HandleAsync(ListAppsQuery query, CancellationToken ct = default)
    {
        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize is < 1 or > 100 ? 20 : query.PageSize;

        var items = await _appRepo.ListByUserAsync(_queryContext.UserId, page, pageSize, query.Search, query.SortField, query.SortDescending, ct);
        var total = await _appRepo.CountByUserAsync(_queryContext.UserId, query.Search, ct);

        return new ListAppsResult { Items = items, Total = total, Page = page, PageSize = pageSize };
    }
}
