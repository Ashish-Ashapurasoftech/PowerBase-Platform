using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
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
    private readonly IRelationshipRepository _relRepo;
    private readonly IPipelineTriggerInterceptor _triggerInterceptor;
    private readonly ITenantUnitOfWork _uow;
    private readonly IMessagePublisher _messagePublisher;

    public DeleteRecordCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IRolePermissionEnforcer enforcer,
        IAuditRepository auditRepo,
        IRelationshipRepository relRepo,
        IPipelineTriggerInterceptor triggerInterceptor,
        ITenantUnitOfWork uow,
        IMessagePublisher messagePublisher)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _enforcer = enforcer;
        _auditRepo = auditRepo;
        _relRepo = relRepo;
        _triggerInterceptor = triggerInterceptor;
        _uow = uow;
        _messagePublisher = messagePublisher;
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

        // One-to-many restrict: block deletion while child records still reference this parent.
        var parentRels = await _relRepo.ListByParentTableAsync(table.Id, ct);
        if (parentRels.Count > 0)
        {
            var ids = await _recordRepo.GetIdsByPublicIdsAsync(table, [command.RecordPublicId], ct);
            await ParentDeleteGuard.EnsureNotReferencedAsync(table, parentRels, ids, _tableRepo, _fieldRepo, _recordRepo, ct);
        }

        var oldRecord = await _recordRepo.GetByPublicIdAsync(table, fields, command.RecordPublicId, ct);
        var oldValuesDict = new Dictionary<long, object?>();
        foreach (var field in fields)
        {
            if (field.Fid.HasValue)
            {
                var colKey = PowerBase.Domain.Constants.PhysicalNaming.GetPhysicalColumnName(field);
                if (oldRecord.TryGetValue(colKey, out var val))
                {
                    oldValuesDict[field.Fid.Value] = val;
                }
            }
        }

        PowerBase.Application.Common.Models.SearchIndexMessage? indexMessage = null;

        await _uow.BeginAsync(ct);
        try
        {
            await _triggerInterceptor.InterceptAsync(table, fields, command.RecordPublicId, oldValuesDict, "record-deleted", ct);

            await _recordRepo.DeleteAsync(table, command.RecordPublicId, _uow.Transaction, ct, msg => indexMessage = msg);
            await _tableRepo.DecrementRecordCountAsync(table.Id, ct);
            await _auditRepo.LogActivityAsync(
                AuditActions.Deleted, AuditEntityTypes.Record, command.RecordPublicId.ToString(), $"Record deleted from {table.Name} with ID {command.RecordPublicId}", appId: table.AppId, ct: ct);

            await _uow.CommitAsync(ct);
        }
        catch
        {
            await _uow.RollbackAsync(ct);
            throw;
        }

        if (indexMessage != null)
        {
            _ = _messagePublisher.PublishAsync(indexMessage, default);
        }
    }
}
