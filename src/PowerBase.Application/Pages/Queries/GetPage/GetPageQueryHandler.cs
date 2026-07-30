using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Pages.Queries.GetPage;

public class GetPageQueryHandler
{
    private readonly IPageRepository _pageRepo;

    public GetPageQueryHandler(IPageRepository pageRepo)
    {
        _pageRepo = pageRepo;
    }

    public async Task<PageDetailDto> HandleAsync(GetPageQuery query, CancellationToken ct = default)
    {
        var page = await _pageRepo.GetByPublicIdAsync(query.PagePublicId, ct);
        var roleIds = await _pageRepo.GetPageRolePublicIdsAsync(page.Id, ct);

        return new PageDetailDto
        {
            Id = page.PublicId,
            PageNumber = page.PageNumber,
            PageType = page.PageType,
            Name = page.Name,
            Description = page.Description,
            Visibility = page.Visibility,
            VisibleToRoleIds = roleIds,
            Definition = page.Definition,
            ContentType = page.ContentType,
            CodeHtml = page.CodeHtml,
            CodeCss = page.CodeCss,
            CodeJs = page.CodeJs,
            IsPublished = page.IsPublished,
            CurrentVersionNo = page.CurrentVersionNo,
            PublishedVersionNo = page.PublishedVersionNo,
            ShowInNav = page.ShowInNav,
            NavOrder = page.NavOrder,
            NavIcon = page.NavIcon,
            IsDefaultHome = page.IsDefaultHome,
            CreatedOn = page.CreatedOn,
            ModifiedOn = page.ModifiedOn,
        };
    }
}
