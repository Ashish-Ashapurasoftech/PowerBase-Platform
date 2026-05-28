using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Commands.UpdateAppRole;

public class UpdateAppRoleCommandHandler
{
    private readonly IAppRoleRepository _appRoleRepo;
    private readonly IAppAccessService _appAccessService;

    public UpdateAppRoleCommandHandler(IAppRoleRepository appRoleRepo, IAppAccessService appAccessService)
    {
        _appRoleRepo = appRoleRepo;
        _appAccessService = appAccessService;
    }

    public async Task HandleAsync(UpdateAppRoleCommand command, CancellationToken ct = default)
    {
        var role = await _appRoleRepo.GetByPublicIdAsync(command.RolePublicId, ct);
        if (role is null)
            throw new NotFoundException("AppRole", command.RolePublicId);

        await _appRoleRepo.SetPermissionsAsync(role.Id, command.Permissions, null, ct);
    }
}
