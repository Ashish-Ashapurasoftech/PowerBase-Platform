using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Enums;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Records.Commands.BulkDeleteRecords;

public class BulkDeleteRecordsCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IRolePermissionEnforcer _enforcer;
    private readonly IAuditRepository _auditRepo;
    private readonly IRelationshipRepository _relRepo;
    private readonly IPipelineTriggerInterceptor _triggerInterceptor;
    private readonly ITenantUnitOfWork _uow;
    private readonly IQueryContext _queryContext;
    private readonly IMessagePublisher _messagePublisher;

    public BulkDeleteRecordsCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IRolePermissionEnforcer enforcer,
        IAuditRepository auditRepo,
        IRelationshipRepository relRepo,
        IPipelineTriggerInterceptor triggerInterceptor,
        ITenantUnitOfWork uow,
        IQueryContext queryContext,
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
        _queryContext = queryContext;
        _messagePublisher = messagePublisher;
    }

    public async Task HandleAsync(BulkDeleteRecordsCommand command, CancellationToken ct = default)
    {
        if (command.RecordPublicIds.Count == 0)
            throw new ValidationException(new Dictionary<string, string[]> { ["ids"] = ["At least one record ID is required."] });
        if (command.RecordPublicIds.Count > 500)
            throw new ValidationException(new Dictionary<string, string[]> { ["ids"] = ["Cannot delete more than 500 records at once."] });

        var uniqueRecordPublicIds = command.RecordPublicIds.Distinct().ToList();

        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);

        var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);
        var access = await _enforcer.GetTableAccessAsync(table, fields, ct);
        if (!access.Unrestricted)
        {
            if (!access.CanDelete)
                throw new UnauthorizedActionException("You do not have permission to delete records from this table.");
            if (access.ViewScope == RecordScopes.OwnRecords || access.ModifyScope == RecordScopes.OwnRecords)
            {
                foreach (var id in uniqueRecordPublicIds)
                    await _enforcer.EnsureRecordOwnedAsync(table, id, ct);
            }
        }

        // One-to-many restrict: block deletion while child records still reference these parents.
        var parentRels = await _relRepo.ListByParentTableAsync(table.Id, ct);
        if (parentRels.Count > 0)
        {
            var ids = await _recordRepo.GetIdsByPublicIdsAsync(table, uniqueRecordPublicIds, ct);
            await ParentDeleteGuard.EnsureNotReferencedAsync(table, parentRels, ids, _tableRepo, _fieldRepo, _recordRepo, ct);
        }

        await _uow.BeginAsync(ct);
        try
        {
            // Load record values for interceptor before they are deleted from DB
            var oldRecordsValues = new List<PipelineRecordChange>();
            foreach (var id in uniqueRecordPublicIds)
            {
                try
                {
                    var valuesDict = await _recordRepo.GetByPublicIdAsync(table, fields, id, ct);
                    // Convert string keys to long field IDs
                    var beforeValues = new Dictionary<long, object?>();
                    foreach (var f in fields)
                    {
                        if (f.Fid.HasValue)
                        {
                            var colKey = PowerBase.Domain.Constants.PhysicalNaming.GetPhysicalColumnName(f);
                            if (valuesDict.TryGetValue(colKey, out var val))
                            {
                                beforeValues[f.Id] = val;
                            }
                        }
                    }
                    oldRecordsValues.Add(new PipelineRecordChange(
                        id,
                        beforeValues,
                        new Dictionary<long, object?>(),
                        new List<long>(),
                        PipelineRecordEventType.Deleted
                    ));
                }
                catch
                {
                    // Skip if not found
                }
            }

            // Intercept triggers
            await _triggerInterceptor.InterceptBulkAsync(
                table,
                fields,
                oldRecordsValues,
                Guid.NewGuid(),
                Guid.NewGuid(),
                _queryContext.UserId,
                ct
            );

            var indexMessages = new List<PowerBase.Application.Common.Models.SearchIndexMessage>();
            await _recordRepo.BulkDeleteAsync(table, uniqueRecordPublicIds, _uow.Transaction, ct, msg => indexMessages.Add(msg));
            await _tableRepo.DecrementRecordCountByAsync(table.Id, uniqueRecordPublicIds.Count, ct);



            await _auditRepo.LogActivityAsync(
                AuditActions.Deleted,
                AuditEntityTypes.Record,
                command.TablePublicId.ToString(),
                $"{uniqueRecordPublicIds.Count} record(s) bulk-deleted from {table.Name}",
                appId: table.AppId,
                ct: ct);

            await _uow.CommitAsync(ct);

            if (indexMessages.Count > 0)
            {
                _ = _messagePublisher.PublishBatchAsync(indexMessages, default);
            }
        }
        catch
        {
            await _uow.RollbackAsync(ct);
            throw;
        }
    }
}
