using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Pages.Queries.ListPageVersions;

public class ListPageVersionsQueryHandler
{
    private readonly IPageRepository _pageRepo;

    public ListPageVersionsQueryHandler(IPageRepository pageRepo)
    {
        _pageRepo = pageRepo;
    }

    public async Task<IReadOnlyList<PageVersionDto>> HandleAsync(ListPageVersionsQuery query, CancellationToken ct = default)
    {
        var versions = await _pageRepo.ListVersionsAsync(query.PagePublicId, ct);
        return versions.Select(v => new PageVersionDto
        {
            Id = v.PublicId,
            VersionNo = v.VersionNo,
            PageType = v.PageType,
            Name = v.Name,
            Description = v.Description,
            Definition = v.Definition,
            CodeHtml = v.CodeHtml,
            CodeCss = v.CodeCss,
            CodeJs = v.CodeJs,
            ChangeNotes = v.ChangeNotes,
            WasPublished = v.WasPublished,
            CreatedOn = v.CreatedOn,
            CreatedBy = v.CreatedBy,
        }).ToList();
    }
}
