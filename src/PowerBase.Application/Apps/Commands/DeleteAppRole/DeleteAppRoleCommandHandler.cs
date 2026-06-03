using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Commands.DeleteAppRole;

public class DeleteAppRoleCommandHandler
{
    private readonly IAppRoleRepository _appRoleRepo;
    private readonly IAuditRepository _auditRepo;

    public DeleteAppRoleCommandHandler(IAppRoleRepository appRoleRepo, IAuditRepository auditRepo)
    {
        _appRoleRepo = appRoleRepo;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(DeleteAppRoleCommand command, CancellationToken ct = default)
    {
        var role = await _appRoleRepo.GetByPublicIdAsync(command.RolePublicId, ct)
            ?? throw new NotFoundException("AppRole", command.RolePublicId);

        if (role.IsSystem)
            throw new UnauthorizedActionException("System roles cannot be deleted.");

        await _appRoleRepo.DeleteAsync(command.RolePublicId, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.Deleted, AuditEntityTypes.AppRole, role.Id.ToString(), $"App role deleted: {role.Name}", appId: role.AppId, ct: ct);
    }
}
