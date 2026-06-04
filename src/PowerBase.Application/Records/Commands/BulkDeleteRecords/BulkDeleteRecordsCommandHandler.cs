using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Records.Commands.BulkDeleteRecords;

public class BulkDeleteRecordsCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IRolePermissionEnforcer _enforcer;

    public BulkDeleteRecordsCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IRolePermissionEnforcer enforcer)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _enforcer = enforcer;
    }

    public async Task HandleAsync(BulkDeleteRecordsCommand command, CancellationToken ct = default)
    {
        if (command.RecordPublicIds.Count == 0)
            throw new ValidationException(new Dictionary<string, string[]> { ["ids"] = ["At least one record ID is required."] });
        if (command.RecordPublicIds.Count > 500)
            throw new ValidationException(new Dictionary<string, string[]> { ["ids"] = ["Cannot delete more than 500 records at once."] });

        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);

        var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);
        var access = await _enforcer.GetTableAccessAsync(table, fields, ct);
        if (!access.Unrestricted)
        {
            if (!access.CanDelete)
                throw new UnauthorizedActionException("You do not have permission to delete records from this table.");
            if (access.ViewScope == RecordScopes.OwnRecords || access.ModifyScope == RecordScopes.OwnRecords)
            {
                foreach (var id in command.RecordPublicIds)
                    await _enforcer.EnsureRecordOwnedAsync(table, id, ct);
            }
        }

        await _recordRepo.BulkDeleteAsync(table, command.RecordPublicIds, ct);
    }
}
