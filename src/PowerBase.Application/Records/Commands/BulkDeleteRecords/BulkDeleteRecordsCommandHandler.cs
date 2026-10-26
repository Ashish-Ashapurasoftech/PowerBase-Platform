using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Enums;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Records.Commands.BulkDeleteRecords;

/// <summary>Everything about a table that does not change while a background delete job runs, loaded once per job.</summary>
public sealed record BulkDeleteJobPlan(
    AppTable Table, IReadOnlyList<AppField> Fields, IReadOnlyList<Relationship> ParentRelationships, bool HasTriggerSubscriptions);

public class BulkDeleteRecordsCommandHandler
{
    /// <summary>Largest id list sent as individual SQL parameters (SQL Server allows 2,100 per statement).</summary>
    private const int ParameterSliceSize = 1000;

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
    private readonly RecordModifyGuard? _modifyGuard;

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
        IMessagePublisher messagePublisher,
        RecordModifyGuard? modifyGuard = null)
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
        _modifyGuard = modifyGuard;
    }

    /// <summary>Background-delete fast path, step 1 (once per job): checks the user may delete from the table and loads
    /// the relationships and trigger-subscription status that every chunk needs.</summary>
    public async Task<BulkDeleteJobPlan> PrepareJobAsync(
        AppTable table, IReadOnlyList<AppField> fields, TableAccessContext access, CancellationToken ct = default)
    {
        if (!access.Unrestricted && !access.CanDelete)
            throw new UnauthorizedActionException("You do not have permission to delete records from this table.");

        var parentRels = await _relRepo.ListByParentTableAsync(table.Id, ct);
        var hasSubscriptions = await _triggerInterceptor.HasActiveSubscriptionsAsync(table, ct);
        return new BulkDeleteJobPlan(table, fields, parentRels, hasSubscriptions);
    }

    /// <summary>Background-delete fast path, step 2 (per chunk): deletes records already chosen by the report's filters,
    /// role scope and Modify rules (so no per-record ownership / rule checks), in one transaction and one UPDATE.
    /// Before-values for pipeline triggers are read — in batches — only when a pipeline listens to this table.</summary>
    public async Task DeleteJobChunkAsync(
        BulkDeleteJobPlan plan, IReadOnlyList<(long Id, Guid PublicId)> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        var table = plan.Table;

        // One-to-many restrict: block deletion while child records still reference these parents.
        if (plan.ParentRelationships.Count > 0)
        {
            for (var i = 0; i < rows.Count; i += ParameterSliceSize)
            {
                var slice = rows.Skip(i).Take(ParameterSliceSize).Select(r => r.Id).ToList();
                await ParentDeleteGuard.EnsureNotReferencedAsync(table, plan.ParentRelationships, slice, _tableRepo, _recordRepo, ct);
            }
        }

        await _uow.BeginAsync(ct);
        try
        {
            if (plan.HasTriggerSubscriptions)
            {
                var changes = new List<PipelineRecordChange>(rows.Count);
                for (var i = 0; i < rows.Count; i += ParameterSliceSize)
                {
                    var slice = rows.Skip(i).Take(ParameterSliceSize).Select(r => r.PublicId).ToList();
                    foreach (var row in await _recordRepo.ListByPublicIdsAsync(table, plan.Fields, slice, ct))
                    {
                        if (row["PublicId"] is not Guid publicId) continue;
                        var beforeValues = new Dictionary<long, object?>();
                        foreach (var f in plan.Fields)
                        {
                            if (!f.Fid.HasValue) continue;
                            if (row.TryGetValue(PowerBase.Domain.Constants.PhysicalNaming.GetPhysicalColumnName(f), out var val))
                                beforeValues[f.Fid.Value] = PowerBase.Application.Pipelines.PipelineEngine.StoredFieldValue(row, f, val);
                        }
                        changes.Add(new PipelineRecordChange(
                            publicId, beforeValues, new Dictionary<long, object?>(), new List<long>(), PipelineRecordEventType.Deleted));
                    }
                }
                await _triggerInterceptor.InterceptBulkAsync(
                    table, plan.Fields, changes, Guid.NewGuid(), Guid.NewGuid(), _queryContext.UserId, ct);
            }

            var indexMessages = new List<SearchIndexMessage>(rows.Count);
            await _recordRepo.BulkDeleteByIdsAsync(table, rows, _uow.Transaction, ct, msg => indexMessages.Add(msg));
            await _tableRepo.DecrementRecordCountByAsync(table.Id, rows.Count, ct);
            await _uow.CommitAsync(ct);

            if (indexMessages.Count > 0)
                _ = _messagePublisher.PublishBatchAsync(indexMessages, default);
        }
        catch
        {
            await _uow.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task HandleAsync(BulkDeleteRecordsCommand command, CancellationToken ct = default)
    {
        if (command.RecordPublicIds.Count == 0)
            throw new ValidationException(new Dictionary<string, string[]> { ["ids"] = ["At least one record ID is required."] });
        if (command.RecordPublicIds.Count > command.MaxRecords)
            throw new ValidationException(new Dictionary<string, string[]> { ["ids"] = [$"Cannot delete more than {command.MaxRecords} records at once."] });

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

            // Modify Custom Rules: only records matching the role's rules may be deleted.
            if (_modifyGuard is not null)
                await _modifyGuard.EnsureAsync(access, table, fields, uniqueRecordPublicIds, ct);
        }

        // One-to-many restrict: block deletion while child records still reference these parents.
        var parentRels = await _relRepo.ListByParentTableAsync(table.Id, ct);
        if (parentRels.Count > 0)
        {
            var ids = await _recordRepo.GetIdsByPublicIdsAsync(table, uniqueRecordPublicIds, ct);
            await ParentDeleteGuard.EnsureNotReferencedAsync(table, parentRels, ids, _tableRepo, _recordRepo, ct);
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
                    var valuesDict = await _recordRepo.GetByPublicIdAsync(table, fields, id, ct: ct);
                    // Convert string keys to long field IDs
                    var beforeValues = new Dictionary<long, object?>();
                    foreach (var f in fields)
                    {
                        if (f.Fid.HasValue)
                        {
                            var colKey = PowerBase.Domain.Constants.PhysicalNaming.GetPhysicalColumnName(f);
                            if (valuesDict.TryGetValue(colKey, out var val))
                            {
                                // A Date / Numeric Range is its two columns, as one { start, end } value.
                                beforeValues[f.Fid.Value] = PowerBase.Application.Pipelines.PipelineEngine.StoredFieldValue(valuesDict, f, val);
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
                catch (NotFoundException)
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



            if (!command.SkipAudit)
            {
                await _auditRepo.LogActivityAsync(
                    AuditActions.Deleted,
                    AuditEntityTypes.Record,
                    command.TablePublicId.ToString(),
                    $"{uniqueRecordPublicIds.Count} record(s) bulk-deleted from {table.Name}",
                    appId: table.AppId,
                    ct: ct);
            }

            await _uow.CommitAsync(ct);

            if (indexMessages.Count > 0)
            {
                _ = _messagePublisher.PublishBatchAsync(indexMessages, default);
            }
        }
        catch
        {
            await _uow.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
