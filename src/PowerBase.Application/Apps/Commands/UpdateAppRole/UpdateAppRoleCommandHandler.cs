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
        await _appAccessService.RequireByAppPublicIdAsync(command.AppPublicId, AppAccess.Admin, ct);

        var affected = await _appRoleRepo.UpdateFlagsAsync(
            command.RolePublicId,
            command.CanViewRecords,
            command.CanAddRecords,
            command.CanEditRecords,
            command.CanDeleteRecords,
            ct);

        if (affected == 0)
            throw new NotFoundException("AppRole", command.RolePublicId);
    }
}
