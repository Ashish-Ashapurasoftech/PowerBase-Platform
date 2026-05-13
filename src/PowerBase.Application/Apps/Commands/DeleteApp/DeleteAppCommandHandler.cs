using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Apps.Commands.DeleteApp;

public class DeleteAppCommandHandler
{
    private readonly IAppRepository _appRepo;

    public DeleteAppCommandHandler(IAppRepository appRepo)
    {
        _appRepo = appRepo;
    }

    public async Task HandleAsync(DeleteAppCommand command, CancellationToken ct = default)
    {
        await _appRepo.DeleteAsync(command.PublicId, ct);
    }
}
