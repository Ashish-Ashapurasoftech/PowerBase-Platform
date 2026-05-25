using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;

namespace PowerBase.Application.Tables.Commands.DeleteTable;

public class DeleteTableCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAuditRepository _auditRepo;

    public DeleteTableCommandHandler(IAppTableRepository tableRepo, IAuditRepository auditRepo)
    {
        _tableRepo = tableRepo;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(DeleteTableCommand command, CancellationToken ct = default)
    {
        await _tableRepo.DeleteAsync(command.PublicId, ct);
        await _auditRepo.LogActivityAsync(AuditActions.Deleted, AuditEntityTypes.AppTable, command.PublicId.ToString(), ct: ct);
    }
}
