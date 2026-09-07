using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Commands.SetDefaultAppRole;

public class SetDefaultAppRoleCommandHandler
{
    private readonly IAppRoleRepository _appRoleRepo;
    private readonly IAppRepository _appRepo;
    private readonly IAppUserRepository _appUserRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly IQueryContext _queryContext;

    public SetDefaultAppRoleCommandHandler(
        IAppRoleRepository appRoleRepo,
        IAppRepository appRepo,
        IAppUserRepository appUserRepo,
        IAuditRepository auditRepo,
        IQueryContext queryContext)
    {
        _appRoleRepo = appRoleRepo;
        _appRepo = appRepo;
        _appUserRepo = appUserRepo;
        _auditRepo = auditRepo;
        _queryContext = queryContext;
    }

    public async Task HandleAsync(SetDefaultAppRoleCommand command, CancellationToken ct = default)
    {
        var app = await _appRepo.GetByPublicIdAsync(command.AppPublicId, ct);
        if (app == null)
            throw new NotFoundException("App", command.AppPublicId);

        var role = await _appRoleRepo.GetByPublicIdAsync(command.RolePublicId, ct);
        if (role is null || role.AppId != app.Id)
            throw new NotFoundException("AppRole", command.RolePublicId);

        // Only Super Admins, Tenant Administrators, the App Owner, or an Administrator-role member may change
        // which role new members are auto-assigned to — same tier as role hierarchy config.
        bool isAuthorizedToConfigure = _queryContext.IsSuperAdmin || _queryContext.IsTenantAdmin || _queryContext.UserId == app.OwnerId;
        if (!isAuthorizedToConfigure)
        {
            var actorRolePublicId = await _appUserRepo.GetUserRolePublicIdAsync(app.Id, _queryContext.UserId, ct);
            if (actorRolePublicId.HasValue)
            {
                var actorRole = await _appRoleRepo.GetByPublicIdAsync(actorRolePublicId.Value, ct);
                if (actorRole?.Name == "Administrator")
                    isAuthorizedToConfigure = true;
            }
        }

        if (!isAuthorizedToConfigure)
            throw new UnauthorizedActionException("Only platform Super Admins, Tenant Administrators, App Owners, or App Administrators can set the default role.");

        await _appRoleRepo.SetDefaultAsync(app.Id, role.Id, ct);
        await _appRepo.SetDefaultRoleAsync(app.Id, role.Id, ct: ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.Updated, AuditEntityTypes.AppRole, role.Id.ToString(), $"Default app role set: {role.Name}", appId: app.Id, ct: ct);
    }
}
