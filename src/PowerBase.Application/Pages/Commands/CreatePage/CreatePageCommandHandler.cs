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
    private readonly ITenantRepository _tenantRepo;
    private readonly CreatePageCommandValidator _validator;

    public CreatePageCommandHandler(
        IAppRepository appRepo, IAppRoleRepository appRoleRepo, IAppUserRepository appUserRepo, IPageRepository pageRepo,
        IQueryContext queryContext, IAuditRepository auditRepo, ITenantRepository tenantRepo)
    {
        _appRepo = appRepo;
        _appRoleRepo = appRoleRepo;
        _appUserRepo = appUserRepo;
        _pageRepo = pageRepo;
        _queryContext = queryContext;
        _auditRepo = auditRepo;
        _tenantRepo = tenantRepo;
        _validator = new CreatePageCommandValidator();
    }

    public async Task<PageDetailDto> HandleAsync(CreatePageCommand command, CancellationToken ct = default)
    {
        var validation = await _validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(
                validation.Errors.GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        var appId = await _appRepo.GetIdByPublicIdAsync(command.AppPublicId, ct);

        if (command.PageType == PageTypes.Code)
        {
            // Outer, tenant-level gate — a Super Admin per-tenant switch (Tenant.CodePagesEnabled,
            // default off). Checked before the app-role permission below: even a Super Admin
            // acting as themselves can't create one until the feature is turned on for this
            // tenant, since the flag exists precisely to let Super Admin opt tenants in.
            var tenant = await _tenantRepo.GetByIdAsync(_queryContext.TenantId, ct);
            if (!tenant.CodePagesEnabled)
                throw new UnauthorizedActionException("Code Pages are not enabled for this tenant.");

            // pages:code is a stricter capability than pages:create — required specifically to
            // author a Code-type page (custom HTML/CSS/JS running in the user's session).
            // It is an APP-role permission (like pages:create), not a tenant-role one.
            if (!_queryContext.IsSuperAdmin)
            {
                var appPermissions = await _appUserRepo.GetUserAppPermissionsAsync(appId, _queryContext.UserId, ct);
                if (!appPermissions.Contains(PermissionCodes.PagesCode))
                    throw new UnauthorizedActionException("Creating a Code page requires the Code Page Builder capability.");
            }
        }

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
