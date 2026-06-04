using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Records.Commands.DeleteRecord;

public class DeleteRecordCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IRolePermissionEnforcer _enforcer;
    private readonly IAuditRepository _auditRepo;

    public DeleteRecordCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IRolePermissionEnforcer enforcer,
        IAuditRepository auditRepo)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _enforcer = enforcer;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(DeleteRecordCommand command, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);

        var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);
        var access = await _enforcer.GetTableAccessAsync(table, fields, ct);
        if (!access.Unrestricted)
        {
            if (!access.CanDelete)
                throw new UnauthorizedActionException("You do not have permission to delete records from this table.");
            if (access.ViewScope == RecordScopes.OwnRecords || access.ModifyScope == RecordScopes.OwnRecords)
                await _enforcer.EnsureRecordOwnedAsync(table, command.RecordPublicId, ct);
        }

        await _recordRepo.DeleteAsync(table, command.RecordPublicId, ct);
        await _tableRepo.DecrementRecordCountAsync(table.Id, ct);
        await _auditRepo.LogActivityAsync(
            AuditActions.Deleted, AuditEntityTypes.Record, command.RecordPublicId.ToString(), $"Record deleted from {table.Name} with ID {command.RecordPublicId}", appId: table.AppId, ct: ct);
    }
}
