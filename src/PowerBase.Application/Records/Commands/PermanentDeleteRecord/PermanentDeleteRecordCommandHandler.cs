using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Records.Commands.PermanentDeleteRecord;

public class PermanentDeleteRecordCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IRolePermissionEnforcer _enforcer;
    private readonly IAuditRepository _auditRepo;
    private readonly ITenantUnitOfWork _uow;
    private readonly IMessagePublisher _messagePublisher;
    private readonly IQueryContext _queryContext;
    private readonly ILogger<PermanentDeleteRecordCommandHandler>? _logger;

    public PermanentDeleteRecordCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IRolePermissionEnforcer enforcer,
        IAuditRepository auditRepo,
        ITenantUnitOfWork uow,
        IMessagePublisher messagePublisher,
        IQueryContext queryContext,
        ILogger<PermanentDeleteRecordCommandHandler>? logger = null)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _enforcer = enforcer;
        _auditRepo = auditRepo;
        _uow = uow;
        _messagePublisher = messagePublisher;
        _queryContext = queryContext;
        _logger = logger;
    }

    public async Task HandleAsync(PermanentDeleteRecordCommand command, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);

        var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);
        var access = await _enforcer.GetTableAccessAsync(table, fields, ct);
        if (!_queryContext.IsSuperAdmin && !access.Unrestricted && !access.CanDelete)
        {
            throw new UnauthorizedActionException("Permanent delete requires administrative privileges.");
        }

        PowerBase.Application.Common.Models.SearchIndexMessage? indexMessage = null;

        await _uow.BeginAsync(ct);
        try
        {
            await _recordRepo.PermanentDeleteAsync(table, command.RecordPublicId, _uow.Transaction, ct, msg => indexMessage = msg);
            await _auditRepo.LogActivityAsync(
                "PermanentlyDeleted",
                AuditEntityTypes.Record,
                command.RecordPublicId.ToString(),
                $"Record permanently deleted from {table.Name} with ID {command.RecordPublicId}",
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
                t => _logger?.LogWarning(t.Exception, "Failed to immediately publish priority search index delete for permanently deleted record {RecordId} in table {TableId}. SearchOutbox will relay.", command.RecordPublicId, table.Id),
                TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
