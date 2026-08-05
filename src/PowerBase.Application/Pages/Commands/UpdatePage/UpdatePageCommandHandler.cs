using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Enums;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pages.Commands.UpdatePage;

public class UpdatePageCommandHandler
{
    private readonly IPageRepository _pageRepo;
    private readonly IAppRoleRepository _appRoleRepo;
    private readonly IAppUserRepository _appUserRepo;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepo;
    private readonly UpdatePageCommandValidator _validator;

    public UpdatePageCommandHandler(
        IPageRepository pageRepo, IAppRoleRepository appRoleRepo, IAppUserRepository appUserRepo,
        IQueryContext queryContext, IAuditRepository auditRepo)
    {
        _pageRepo = pageRepo;
        _appRoleRepo = appRoleRepo;
        _appUserRepo = appUserRepo;
        _queryContext = queryContext;
        _auditRepo = auditRepo;
        _validator = new UpdatePageCommandValidator();
    }

    public async Task<PageDetailDto> HandleAsync(UpdatePageCommand command, CancellationToken ct = default)
    {
        var validation = await _validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(
                validation.Errors.GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        var page = await _pageRepo.GetByPublicIdAsync(command.PagePublicId, ct);

        if (page.PageType == PageTypes.Code && !_queryContext.IsSuperAdmin
            && !_queryContext.Permissions.Contains(PermissionCodes.PagesCode))
            throw new UnauthorizedActionException("Editing a Code page requires the Code Page Builder capability.");

        // Snapshot the PRE-EDIT state at the current version number — version N is always
        // "what the page looked like before edit N", which is what Restore needs and matches
        // how the change note reads ("what changed to produce version N+1").
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

        page.Name = command.Name;
        page.Description = command.Description;
        page.Visibility = command.Visibility;
        if (page.PageType == PageTypes.Dashboard)
            page.Definition = command.Definition ?? page.Definition;
        if (page.PageType == PageTypes.Code)
        {
            page.ContentType = command.ContentType;
            page.CodeHtml = command.CodeHtml;
            page.CodeCss = command.CodeCss;
            page.CodeJs = command.CodeJs;
        }
        page.ShowInNav = command.ShowInNav;
        page.NavOrder = command.NavOrder;
        page.NavIcon = command.NavIcon;
        page.CurrentVersionNo += 1;

        await _pageRepo.UpdateAsync(page, ct);

        var rolesToSave = await ResolvePageRoleIdsAsync(command.Visibility, command.VisibleToRoleIds, page.AppId, page.OwnerId, ct);
        await _pageRepo.ReplacePageRolesAsync(page.Id, rolesToSave, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.SchemaChanged, AuditEntityTypes.Page, page.PublicId.ToString(),
            $"Page updated: {page.Name}", appId: page.AppId,
            newValues: System.Text.Json.JsonSerializer.Serialize(new { command.ChangeNotes }), ct: ct);

        return new PageDetailDto
        {
            Id = page.PublicId,
            PageNumber = page.PageNumber,
            PageType = page.PageType,
            Name = page.Name,
            Description = page.Description,
            Visibility = page.Visibility,
            VisibleToRoleIds = command.VisibleToRoleIds ?? [],
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

    private async Task<List<long>> ResolvePageRoleIdsAsync(string visibility, IReadOnlyList<Guid>? visibleToRoleIds, long appId, long ownerId, CancellationToken ct)
    {
        var result = new List<long>();
        if (visibility == Visibility.SpecificRoles.ToString() && visibleToRoleIds?.Count > 0)
        {
            foreach (var rolePubId in visibleToRoleIds)
            {
                var role = await _appRoleRepo.GetByPublicIdAsync(rolePubId, ct);
                if (role is not null) result.Add(role.Id);
            }
        }
        else if (visibility == Visibility.MyRole.ToString())
        {
            // "My Role" means visible to everyone sharing the page owner's role — resolve and
            // pin that role now, since AppRolePage is the only thing GetVisiblePageAsync's
            // visibility predicate checks for MyRole/SpecificRoles pages. Without this, a page
            // set to MyRole would have no role rows at all and become invisible to everyone,
            // including its own owner.
            var ownerAppUser = await _appUserRepo.GetByAppAndUserAsync(appId, ownerId, ct);
            if (ownerAppUser is not null) result.Add(ownerAppUser.AppRoleId);
        }
        return result;
    }
}
