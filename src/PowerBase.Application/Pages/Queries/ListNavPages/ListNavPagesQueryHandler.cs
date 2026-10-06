using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Pages.Queries.ListNavPages;

public record NavPageResult(Guid Id, string Name, string PageType, string? NavIcon, int PageNumber);

/// <summary>Backs the app sidebar's Pages section — every published, ShowInNav=true page the
/// CURRENT user can actually see (role/visibility-filtered server-side in
/// IPageRepository.ListNavPagesAsync's own SQL, same predicate GetVisiblePageAsync uses
/// elsewhere), ordered by NavOrder then Name. This repository method existed already but had no
/// caller anywhere in the API — this handler + its controller endpoint are what makes the
/// spec's "navigation integration" bullet actually reachable from the frontend.</summary>
public class ListNavPagesQueryHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IPageRepository _pageRepo;

    public ListNavPagesQueryHandler(IAppRepository appRepo, IPageRepository pageRepo)
    {
        _appRepo = appRepo;
        _pageRepo = pageRepo;
    }

    public async Task<IReadOnlyList<NavPageResult>> HandleAsync(ListNavPagesQuery query, CancellationToken ct = default)
    {
        var appId = await _appRepo.GetIdByPublicIdAsync(query.AppPublicId, ct);
        var pages = await _pageRepo.ListNavPagesAsync(appId, ct);
        return pages.Select(p => new NavPageResult(p.PublicId, p.Name, p.PageType, p.NavIcon, p.PageNumber)).ToList();
    }
}
