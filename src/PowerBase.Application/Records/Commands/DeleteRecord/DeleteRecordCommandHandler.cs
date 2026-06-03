using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;

namespace PowerBase.Application.Records.Commands.DeleteRecord;

public class DeleteRecordCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IAuditRepository _auditRepo;

    public DeleteRecordCommandHandler(IAppTableRepository tableRepo, IRecordRepository recordRepo, IAuditRepository auditRepo)
    {
        _tableRepo = tableRepo;
        _recordRepo = recordRepo;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(DeleteRecordCommand command, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);
        await _recordRepo.DeleteAsync(table, command.RecordPublicId, ct);
        await _tableRepo.DecrementRecordCountAsync(table.Id, ct);
        await _auditRepo.LogActivityAsync(
            AuditActions.Deleted, AuditEntityTypes.Record, command.RecordPublicId.ToString(), $"Record deleted from {table.Name} with ID {command.RecordPublicId}", appId: table.AppId, ct: ct);
    }
}
