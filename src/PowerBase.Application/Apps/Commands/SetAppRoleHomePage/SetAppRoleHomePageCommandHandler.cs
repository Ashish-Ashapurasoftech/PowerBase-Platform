using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Commands.SetAppRoleHomePage;

public class SetAppRoleHomePageCommandHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppRoleRepository _appRoleRepo;
    private readonly IPageRepository _pageRepo;
    private readonly IAuditRepository _auditRepo;

    public SetAppRoleHomePageCommandHandler(
        IAppRepository appRepo, IAppRoleRepository appRoleRepo, IPageRepository pageRepo, IAuditRepository auditRepo)
    {
        _appRepo = appRepo;
        _appRoleRepo = appRoleRepo;
        _pageRepo = pageRepo;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(SetAppRoleHomePageCommand command, CancellationToken ct = default)
    {
        var appId = await _appRepo.GetIdByPublicIdAsync(command.AppPublicId, ct);

        var role = await _appRoleRepo.GetByPublicIdAsync(command.RolePublicId, ct);
        if (role is null || role.AppId != appId)
            throw new NotFoundException("AppRole", command.RolePublicId);

        long? pageId = null;
        if (command.PagePublicId.HasValue)
        {
            var page = await _pageRepo.GetByPublicIdAsync(command.PagePublicId.Value, ct);
            if (page.AppId != appId)
                throw new NotFoundException("Page", command.PagePublicId.Value);
            if (page.PageType != PageTypes.Dashboard)
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["pagePublicId"] = ["Only a Dashboard page can be a role's home page."]
                });
            pageId = page.Id;
        }

        await _appRoleRepo.SetHomePageAsync(role.Id, pageId, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.Updated, AuditEntityTypes.AppRole, role.Id.ToString(),
            pageId.HasValue ? $"Home page set for role: {role.Name}" : $"Home page cleared for role: {role.Name}",
            appId: appId, ct: ct);
    }
}
