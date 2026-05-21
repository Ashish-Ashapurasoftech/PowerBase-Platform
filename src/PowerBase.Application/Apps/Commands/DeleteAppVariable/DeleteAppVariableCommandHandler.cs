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
        await _appAccessService.RequireByAppPublicIdAsync(command.AppPublicId, AppAccess.Admin, ct);

        var appId = await _appRepo.GetIdByPublicIdAsync(command.AppPublicId, ct);

        await _variableRepo.DeleteAsync(appId, command.PublicId, ct);
    }
}
