using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Commands.DeleteAppRole;

public class DeleteAppRoleCommandHandler
{
    private readonly IAppRoleRepository _appRoleRepo;

    public DeleteAppRoleCommandHandler(IAppRoleRepository appRoleRepo)
    {
        _appRoleRepo = appRoleRepo;
    }

    public async Task HandleAsync(DeleteAppRoleCommand command, CancellationToken ct = default)
    {
        var role = await _appRoleRepo.GetByPublicIdAsync(command.RolePublicId, ct)
            ?? throw new NotFoundException("AppRole", command.RolePublicId);

        if (role.IsSystem)
            throw new UnauthorizedActionException("System roles cannot be deleted.");

        await _appRoleRepo.DeleteAsync(command.RolePublicId, ct);
    }
}
