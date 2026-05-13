using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Apps.Queries.ListApps;

public class ListAppsResult
{
    public IReadOnlyList<App> Items { get; init; } = Array.Empty<App>();
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
}

public class ListAppsQueryHandler
{
    private readonly IAppRepository _appRepo;

    public ListAppsQueryHandler(IAppRepository appRepo)
    {
        _appRepo = appRepo;
    }

    public async Task<ListAppsResult> HandleAsync(ListAppsQuery query, CancellationToken ct = default)
    {
        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize is < 1 or > 100 ? 20 : query.PageSize;

        var items = await _appRepo.ListAsync(page, pageSize, ct);
        var total = await _appRepo.CountAsync(ct);

        return new ListAppsResult { Items = items, Total = total, Page = page, PageSize = pageSize };
    }
}
