using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Records.Commands.RestoreRecord;

public class RestoreRecordCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IRolePermissionEnforcer _enforcer;
    private readonly IAuditRepository _auditRepo;
    private readonly ITenantUnitOfWork _uow;
    private readonly IMessagePublisher _messagePublisher;
    private readonly ILogger<RestoreRecordCommandHandler>? _logger;

    public RestoreRecordCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IRolePermissionEnforcer enforcer,
        IAuditRepository auditRepo,
        ITenantUnitOfWork uow,
        IMessagePublisher messagePublisher,
        ILogger<RestoreRecordCommandHandler>? logger = null)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _enforcer = enforcer;
        _auditRepo = auditRepo;
        _uow = uow;
        _messagePublisher = messagePublisher;
        _logger = logger;
    }

    public async Task HandleAsync(RestoreRecordCommand command, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);

        var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);
        var access = await _enforcer.GetTableAccessAsync(table, fields, ct);
        if (!access.Unrestricted)
        {
            if (!access.CanDelete && access.ModifyScope == RecordScopes.None)
                throw new UnauthorizedActionException("You do not have permission to restore records in this table.");
        }

        PowerBase.Application.Common.Models.SearchIndexMessage? indexMessage = null;

        await _uow.BeginAsync(ct);
        try
        {
            await _recordRepo.RestoreAsync(table, command.RecordPublicId, _uow.Transaction, ct, msg => indexMessage = msg);
            await _tableRepo.IncrementRecordCountAsync(table.Id, ct);
            await _auditRepo.LogActivityAsync(
                "Restored",
                AuditEntityTypes.Record,
                command.RecordPublicId.ToString(),
                $"Record restored in {table.Name} with ID {command.RecordPublicId}",
                appId: table.AppId,
                ct: ct);

            await _uow.CommitAsync(ct);
        }
        catch
        {
            await _uow.RollbackAsync(CancellationToken.None);
            throw;
        }

        if (indexMessage != null)
        {
            _ = _messagePublisher.PublishPriorityAsync(indexMessage, default).ContinueWith(
                t => _logger?.LogWarning(t.Exception, "Failed to immediately publish priority search index message for restored record {RecordId} in table {TableId}. SearchOutbox will relay.", command.RecordPublicId, table.Id),
                TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
