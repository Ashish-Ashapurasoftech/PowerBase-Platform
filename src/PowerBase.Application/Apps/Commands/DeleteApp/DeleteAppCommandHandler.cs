using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;

namespace PowerBase.Application.Apps.Commands.DeleteApp;

public class DeleteAppCommandHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAuditRepository _auditRepo;

    public DeleteAppCommandHandler(IAppRepository appRepo, IAuditRepository auditRepo)
    {
        _appRepo = appRepo;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(DeleteAppCommand command, CancellationToken ct = default)
    {
        await _appRepo.DeleteAsync(command.PublicId, ct);
        await _auditRepo.LogActivityAsync(AuditActions.Deleted, AuditEntityTypes.App, command.PublicId.ToString(), ct: ct);
    }
}
