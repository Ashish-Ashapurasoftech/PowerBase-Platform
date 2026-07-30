using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Pages.Queries.ListPages;

public class ListPagesQueryHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IPageRepository _pageRepo;

    public ListPagesQueryHandler(IAppRepository appRepo, IPageRepository pageRepo)
    {
        _appRepo = appRepo;
        _pageRepo = pageRepo;
    }

    public async Task<IReadOnlyList<PageListItemDto>> HandleAsync(ListPagesQuery query, CancellationToken ct = default)
    {
        var appId = await _appRepo.GetIdByPublicIdAsync(query.AppPublicId, ct);
        var pages = query.AllPages
            ? await _pageRepo.ListAllByAppAsync(appId, query.Search, ct)
            : await _pageRepo.ListVisibleByAppAsync(appId, query.Search, ct);

        var homePageRoles = await _pageRepo.GetHomePageRoleNamesAsync(appId, ct);

        return pages.Select(p => new PageListItemDto
        {
            Id = p.PublicId,
            PageNumber = p.PageNumber,
            PageType = p.PageType,
            Name = p.Name,
            Description = p.Description,
            Visibility = p.Visibility,
            IsPublished = p.IsPublished,
            ShowInNav = p.ShowInNav,
            NavOrder = p.NavOrder,
            IsDefaultHome = p.IsDefaultHome,
            HomePageForRoles = homePageRoles.TryGetValue(p.Id, out var roles) ? roles : [],
            CreatedOn = p.CreatedOn,
            ModifiedOn = p.ModifiedOn,
        }).ToList();
    }
}
