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
        var table = await _tableRepo.GetByPublicIdAsync(command.PublicId, ct);
        await _tableRepo.DeleteAsync(command.PublicId, ct);
        await _auditRepo.LogActivityAsync(AuditActions.Deleted, AuditEntityTypes.AppTable, command.PublicId.ToString(), $"Table deleted: {table.Name}", appId: table.AppId, ct: ct);
    }
}
