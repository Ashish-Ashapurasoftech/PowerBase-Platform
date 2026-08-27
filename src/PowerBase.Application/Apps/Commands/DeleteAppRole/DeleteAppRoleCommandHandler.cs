using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Commands.DeleteAppRole;

public class DeleteAppRoleCommandHandler
{
    private readonly IAppRoleRepository _appRoleRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly IQueryContext _queryContext;
    private readonly IAppUserRepository _appUserRepo;

    public DeleteAppRoleCommandHandler(
        IAppRoleRepository appRoleRepo, 
        IAuditRepository auditRepo,
        IQueryContext queryContext,
        IAppUserRepository appUserRepo)
    {
        _appRoleRepo = appRoleRepo;
        _auditRepo = auditRepo;
        _queryContext = queryContext;
        _appUserRepo = appUserRepo;
    }

    public async Task HandleAsync(DeleteAppRoleCommand command, CancellationToken ct = default)
    {
        var role = await _appRoleRepo.GetByPublicIdAsync(command.RolePublicId, ct)
            ?? throw new NotFoundException("AppRole", command.RolePublicId);

        var currentUserRolePublicId = await _appUserRepo.GetUserRolePublicIdAsync(role.AppId, _queryContext.UserId, ct);
        if (currentUserRolePublicId == command.RolePublicId)
        {
            throw new UnauthorizedActionException("delete your own app role");
        }

        if (!_queryContext.IsSuperAdmin)
        {
            if (!currentUserRolePublicId.HasValue)
            {
                throw new UnauthorizedActionException("You are not a member of this application.");
            }

            var actorRole = await _appRoleRepo.GetByPublicIdAsync(currentUserRolePublicId.Value, ct);
            if (actorRole == null)
            {
                throw new UnauthorizedActionException("Your role was not found.");
            }

            // Hard Rule: Target role's rank must be strictly greater than actor's rank
            int actorRank = actorRole.Rank ?? int.MaxValue;
            int targetRank = role.Rank ?? int.MaxValue;
            if (targetRank <= actorRank)
            {
                throw new UnauthorizedActionException("You cannot manage a role equal to or above your own.");
            }

            // Configured setting check
            if (actorRole.ManageableRolesType == "None")
            {
                throw new UnauthorizedActionException("Your role is not allowed to manage any roles.");
            }
            else if (actorRole.ManageableRolesType == "Manual")
            {
                var manageableIds = await _appRoleRepo.GetManageableRolePublicIdsAsync(actorRole.Id, ct);
                if (!manageableIds.Contains(role.PublicId))
                {
                    throw new UnauthorizedActionException("Your role is not allowed to manage this role.");
                }
            }
        }

        if (role.IsSystem)
            throw new UnauthorizedActionException("System roles cannot be deleted.");

        await _appRoleRepo.DeleteAsync(command.RolePublicId, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.Deleted, AuditEntityTypes.AppRole, role.Id.ToString(), $"App role deleted: {role.Name}", appId: role.AppId, ct: ct);
    }
}
