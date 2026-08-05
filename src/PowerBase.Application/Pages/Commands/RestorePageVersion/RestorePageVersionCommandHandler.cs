using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pages.Commands.RestorePageVersion;

public class RestorePageVersionCommandHandler
{
    private readonly IPageRepository _pageRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly RestorePageVersionCommandValidator _validator;

    public RestorePageVersionCommandHandler(IPageRepository pageRepo, IAuditRepository auditRepo)
    {
        _pageRepo = pageRepo;
        _auditRepo = auditRepo;
        _validator = new RestorePageVersionCommandValidator();
    }

    public async Task<PageDetailDto> HandleAsync(RestorePageVersionCommand command, CancellationToken ct = default)
    {
        var validation = await _validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(
                validation.Errors.GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        var page = await _pageRepo.GetByPublicIdAsync(command.PagePublicId, ct);
        var target = await _pageRepo.GetVersionAsync(command.PagePublicId, command.VersionNo, ct)
            ?? throw new NotFoundException("PageVersion", command.VersionNo);

        // Snapshot the CURRENT (pre-restore) state at CurrentVersionNo — same "snapshot before
        // mutating" rule as a normal edit — so restoring is itself undoable via another restore.
        await _pageRepo.InsertVersionAsync(new PageVersion
        {
            PageId = page.Id,
            VersionNo = page.CurrentVersionNo,
            PageType = page.PageType,
            Name = page.Name,
            Description = page.Description,
            Definition = page.Definition,
            CodeHtml = page.CodeHtml,
            CodeCss = page.CodeCss,
            CodeJs = page.CodeJs,
            ChangeNotes = command.ChangeNotes,
            WasPublished = page.IsPublished,
        }, ct);

        page.Name = target.Name;
        page.Description = target.Description;
        page.Definition = target.Definition;
        page.CodeHtml = target.CodeHtml;
        page.CodeCss = target.CodeCss;
        page.CodeJs = target.CodeJs;
        page.CurrentVersionNo += 1;

        await _pageRepo.UpdateAsync(page, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.VersionRestored, AuditEntityTypes.Page, page.PublicId.ToString(),
            $"Page '{page.Name}' restored to version {command.VersionNo}", appId: page.AppId, ct: ct);

        return new PageDetailDto
        {
            Id = page.PublicId,
            PageNumber = page.PageNumber,
            PageType = page.PageType,
            Name = page.Name,
            Description = page.Description,
            Visibility = page.Visibility,
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
            ModifiedOn = DateTime.UtcNow,
        };
    }
}
