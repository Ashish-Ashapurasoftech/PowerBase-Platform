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

        if (role.IsSystem)
            throw new UnauthorizedActionException("System roles cannot be deleted.");

        await _appRoleRepo.DeleteAsync(command.RolePublicId, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.Deleted, AuditEntityTypes.AppRole, role.Id.ToString(), $"App role deleted: {role.Name}", appId: role.AppId, ct: ct);
    }
}
