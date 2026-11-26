using PowerBase.Application.Apps.RoleUi;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Commands.UpdateRoleUiSettings;

public record UpdateRoleUiSettingsCommand(Guid AppPublicId, Guid RolePublicId, RoleUiSettings Settings);

/// <summary>
/// Saves a role's "User interface" settings. Same role-hierarchy rules as saving the role's table permissions:
/// Super Admin / Administrator may edit any role; anyone else only a role ranked below theirs that their role
/// may manage, and never their own role.
/// </summary>
public class UpdateRoleUiSettingsCommandHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppRoleRepository _appRoleRepo;
    private readonly IAppUserRepository _appUserRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly IQueryContext _queryContext;

    public UpdateRoleUiSettingsCommandHandler(
        IAppRepository appRepo, IAppRoleRepository appRoleRepo, IAppUserRepository appUserRepo,
        IAuditRepository auditRepo, IQueryContext queryContext)
    {
        _appRepo = appRepo;
        _appRoleRepo = appRoleRepo;
        _appUserRepo = appUserRepo;
        _auditRepo = auditRepo;
        _queryContext = queryContext;
    }

    public async Task HandleAsync(UpdateRoleUiSettingsCommand command, CancellationToken ct = default)
    {
        var appId = await _appRepo.GetIdByPublicIdAsync(command.AppPublicId, ct);
        var role = await _appRoleRepo.GetByPublicIdAsync(command.RolePublicId, ct);
        if (role is null || role.AppId != appId)
            throw new NotFoundException("AppRole", command.RolePublicId);

        await EnsureCanManageAsync(role, ct);

        await _appRoleRepo.SetUiSettingsJsonAsync(role.Id, RoleUiSettings.Serialize(command.Settings ?? RoleUiSettings.Empty), ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.Updated, AuditEntityTypes.AppRole, role.Id.ToString(),
            $"User interface settings updated for role: {role.Name}", appId: appId, ct: ct);
    }

    private async Task EnsureCanManageAsync(AppRole role, CancellationToken ct)
    {
        var currentUserRolePublicId = await _appUserRepo.GetUserRolePublicIdAsync(role.AppId, _queryContext.UserId, ct);
        AppRole? actorRole = currentUserRolePublicId.HasValue
            ? await _appRoleRepo.GetByPublicIdAsync(currentUserRolePublicId.Value, ct)
            : null;
        if (_queryContext.IsSuperAdmin || actorRole?.Name == "Administrator") return;

        if (currentUserRolePublicId == role.PublicId)
            throw new UnauthorizedActionException("modify the user interface settings of your own app role");
        if (actorRole == null)
            throw new UnauthorizedActionException("Your role was not found.");

        int actorRank = actorRole.Rank ?? int.MaxValue;
        int targetRank = role.Rank ?? int.MaxValue;
        if (targetRank <= actorRank)
            throw new UnauthorizedActionException("You cannot manage a role equal to or above your own.");

        if (actorRole.ManageableRolesType == "None")
            throw new UnauthorizedActionException("Your role is not allowed to manage any roles.");
        if (actorRole.ManageableRolesType == "Manual")
        {
            var manageableIds = await _appRoleRepo.GetManageableRolePublicIdsAsync(actorRole.Id, ct);
            if (!manageableIds.Contains(role.PublicId))
                throw new UnauthorizedActionException("Your role is not allowed to manage this role.");
        }
    }
}
