using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Pages.Commands.DuplicatePage;

public class DuplicatePageCommandHandler
{
    private readonly IPageRepository _pageRepo;
    private readonly IAuditRepository _auditRepo;

    public DuplicatePageCommandHandler(IPageRepository pageRepo, IAuditRepository auditRepo)
    {
        _pageRepo = pageRepo;
        _auditRepo = auditRepo;
    }

    public async Task<PageDetailDto> HandleAsync(DuplicatePageCommand command, CancellationToken ct = default)
    {
        var source = await _pageRepo.GetByPublicIdAsync(command.PagePublicId, ct);
        var newName = string.IsNullOrWhiteSpace(command.NewName) ? $"{source.Name} (copy)" : command.NewName;

        var (pageId, publicId, pageNumber) = await _pageRepo.DuplicateAsync(command.PagePublicId, newName, ct);

        // Duplicated pages start Personal to the duplicating user (PageRepository.DuplicateAsync
        // already resets OwnerId) and carry no role visibility rows — a deliberate default,
        // matching Personal being the safest starting visibility. Deliberately NOT writing a
        // PageVersion row here either — same reasoning as CreatePageCommandHandler: the new
        // copy's CurrentVersionNo starts at 1 with no history row yet, so the first edit's
        // pre-edit snapshot (also at VersionNo 1) doesn't collide on the (PageId, VersionNo) PK.

        await _auditRepo.LogActivityAsync(
            AuditActions.Created, AuditEntityTypes.Page, publicId.ToString(),
            $"Page duplicated: {newName} (from {source.Name})", appId: source.AppId, ct: ct);

        return new PageDetailDto
        {
            Id = publicId,
            PageNumber = pageNumber,
            PageType = source.PageType,
            Name = newName,
            Description = source.Description,
            Visibility = "Personal",
            VisibleToRoleIds = [],
            Definition = source.Definition,
            ContentType = source.ContentType,
            CodeHtml = source.CodeHtml,
            CodeCss = source.CodeCss,
            CodeJs = source.CodeJs,
            IsPublished = false,
            CurrentVersionNo = 1,
            PublishedVersionNo = null,
            ShowInNav = false,
            NavOrder = source.NavOrder,
            NavIcon = source.NavIcon,
            IsDefaultHome = false,
            CreatedOn = DateTime.UtcNow,
            ModifiedOn = null,
        };
    }
}
