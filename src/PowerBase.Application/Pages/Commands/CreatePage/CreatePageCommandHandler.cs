using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Enums;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pages.Commands.CreatePage;

public class CreatePageCommandHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppRoleRepository _appRoleRepo;
    private readonly IAppUserRepository _appUserRepo;
    private readonly IPageRepository _pageRepo;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepo;
    private readonly CreatePageCommandValidator _validator;

    public CreatePageCommandHandler(
        IAppRepository appRepo, IAppRoleRepository appRoleRepo, IAppUserRepository appUserRepo, IPageRepository pageRepo,
        IQueryContext queryContext, IAuditRepository auditRepo)
    {
        _appRepo = appRepo;
        _appRoleRepo = appRoleRepo;
        _appUserRepo = appUserRepo;
        _pageRepo = pageRepo;
        _queryContext = queryContext;
        _auditRepo = auditRepo;
        _validator = new CreatePageCommandValidator();
    }

    public async Task<PageDetailDto> HandleAsync(CreatePageCommand command, CancellationToken ct = default)
    {
        var validation = await _validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(
                validation.Errors.GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        // pages:code is a stricter capability than pages:create — required specifically to
        // author a Code-type page (custom HTML/CSS/JS running in the user's session).
        if (command.PageType == PageTypes.Code && !_queryContext.IsSuperAdmin
            && !_queryContext.Permissions.Contains(PermissionCodes.PagesCode))
            throw new UnauthorizedActionException("Creating a Code page requires the Code Page Builder capability.");

        var appId = await _appRepo.GetIdByPublicIdAsync(command.AppPublicId, ct);

        var page = new Page
        {
            AppId = appId,
            PageType = command.PageType,
            Name = command.Name,
            Description = command.Description,
            OwnerId = _queryContext.UserId,
            Visibility = command.Visibility,
            Definition = command.PageType == PageTypes.Dashboard ? (command.Definition ?? "{}") : "{}",
            ContentType = command.PageType == PageTypes.Code ? command.ContentType : null,
            CodeHtml = command.PageType == PageTypes.Code ? command.CodeHtml : null,
            CodeCss = command.PageType == PageTypes.Code ? command.CodeCss : null,
            CodeJs = command.PageType == PageTypes.Code ? command.CodeJs : null,
            ShowInNav = command.ShowInNav,
            NavOrder = command.NavOrder,
            NavIcon = command.NavIcon,
        };

        var (pageId, publicId, pageNumber) = await _pageRepo.CreateAsync(page, ct);

        var rolesToSave = await ResolvePageRoleIdsAsync(command.Visibility, command.VisibleToRoleIds, appId, _queryContext.UserId, ct);
        if (rolesToSave.Count > 0)
            await _pageRepo.ReplacePageRolesAsync(pageId, rolesToSave, ct);

        // Deliberately NOT writing a PageVersion row here. CurrentVersionNo starts at 1,
        // meaning "the live row IS version 1 — nothing has been snapshotted into history yet".
        // UpdatePageCommandHandler snapshots the pre-edit state at CurrentVersionNo before its
        // first edit, which — for a freshly created page — is this exact as-created content, at
        // VersionNo 1. Pre-inserting a version-1 row here would collide with that first snapshot
        // (PK is (PageId, VersionNo)) the moment the page is edited for the first time.

        await _auditRepo.LogActivityAsync(
            AuditActions.Created, AuditEntityTypes.Page, publicId.ToString(),
            $"Page created: {command.Name}", appId: appId, ct: ct);

        return Map(page, publicId, pageNumber, rolesToSave.Count > 0 ? command.VisibleToRoleIds! : []);
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
            // See UpdatePageCommandHandler.ResolvePageRoleIdsAsync — MyRole must pin the
            // owner's role into AppRolePage now, or the page becomes invisible to everyone.
            var ownerAppUser = await _appUserRepo.GetByAppAndUserAsync(appId, ownerId, ct);
            if (ownerAppUser is not null) result.Add(ownerAppUser.AppRoleId);
        }
        return result;
    }

    private static PageDetailDto Map(Page page, Guid publicId, int pageNumber, IReadOnlyList<Guid> visibleToRoleIds) => new()
    {
        Id = publicId,
        PageNumber = pageNumber,
        PageType = page.PageType,
        Name = page.Name,
        Description = page.Description,
        Visibility = page.Visibility,
        VisibleToRoleIds = visibleToRoleIds,
        Definition = page.Definition,
        ContentType = page.ContentType,
        CodeHtml = page.CodeHtml,
        CodeCss = page.CodeCss,
        CodeJs = page.CodeJs,
        IsPublished = false,
        CurrentVersionNo = 1,
        PublishedVersionNo = null,
        ShowInNav = page.ShowInNav,
        NavOrder = page.NavOrder,
        NavIcon = page.NavIcon,
        IsDefaultHome = false,
        CreatedOn = DateTime.UtcNow,
        ModifiedOn = null,
    };
}
