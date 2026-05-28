using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Apps.Commands.DeleteAppVariable;

public class DeleteAppVariableCommandHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppVariableRepository _variableRepo;
    private readonly IAppAccessService _appAccessService;

    public DeleteAppVariableCommandHandler(
        IAppRepository appRepo,
        IAppVariableRepository variableRepo,
        IAppAccessService appAccessService)
    {
        _appRepo = appRepo;
        _variableRepo = variableRepo;
        _appAccessService = appAccessService;
    }

    public async Task HandleAsync(DeleteAppVariableCommand command, CancellationToken ct = default)
    {
        // Enforce App Administrator privileges explicitly
        await _appAccessService.RequireAppRoleAsync(command.AppPublicId, "Administrator", ct);

        var appId = await _appRepo.GetIdByPublicIdAsync(command.AppPublicId, ct);
        var variable = await _variableRepo.GetByPublicIdAsync(appId, command.PublicId, ct);

        await _variableRepo.DeleteAsync(appId, command.PublicId, ct);
    }
}
