using System.Linq;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Application.FieldReferences;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Fields.Commands.DeleteField;

public class DeleteFieldCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IPipelineRepository _pipelineRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly ITenantUnitOfWork _uow;
    private readonly IRelationshipRepository _relRepo;
    private readonly IFieldReferenceIndexer _refIndexer;
    private readonly IMessagePublisher? _publisher;
    private readonly IQueryContext? _queryContext;

    public DeleteFieldCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IPipelineRepository pipelineRepo,
        IAuditRepository auditRepo,
        ITenantUnitOfWork uow,
        IRelationshipRepository relRepo,
        IFieldReferenceIndexer? refIndexer = null,
        IMessagePublisher? publisher = null,
        IQueryContext? queryContext = null)
    {
        _publisher = publisher;
        _queryContext = queryContext;
        _relRepo = relRepo;
        _refIndexer = refIndexer ?? NullFieldReferenceIndexer.Instance;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _pipelineRepo = pipelineRepo;
        _auditRepo = auditRepo;
        _uow = uow;
    }

    public async Task HandleAsync(DeleteFieldCommand command, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);

        var field = await _fieldRepo.GetByPublicIdAsync(command.FieldPublicId, ct)
            ?? throw new NotFoundException("Field", command.FieldPublicId);

        // Defend against a caller supplying a field PublicId from a different table than the one
        // named in the route (and thus than the one the permission check above just authorized) —
        // same guard UpdateFieldCommandHandler applies, since GetByPublicIdAsync alone isn't
        // table-scoped.
        if (field.AppTableId != table.Id)
            throw new NotFoundException("Field", command.FieldPublicId);

        if (field.IsSystem)
            throw new UnauthorizedActionException("System fields cannot be deleted.");

        // The columns the field used in the search index, read now: once it is deleted the table no longer says.
        var indexCleanup = _queryContext == null
            ? []
            : PowerBase.Application.Search.FieldDeleteIndexCleanup.Capture(_queryContext.TenantId, table, await _fieldRepo.ListByTableAsync(table.Id, ct), [field]);

        await RelationshipKeyFieldDeleteGuard.EnsureNotRelationshipKeyAsync(table, [field], _relRepo, ct);
        var tableFields = await _fieldRepo.ListByTableAsync(table.Id, ct);
        ReferenceFilterDependencyGuard.EnsureNotControlling(table, tableFields, [field]);
        Common.SnapshotSourceDependencyGuard.EnsureNotSource(tableFields, [field]);

        // Dependency Check: Pipeline Field Lock
        if (field.Fid.HasValue)
        {
            // Another account's PowerFlow can't be deactivated from here, so this blocks even a forced delete.
            if (await _pipelineRepo.GetCrossAccountTriggerCountAsync(table.PublicId, new[] { field.Fid.Value }, ct) > 0)
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    { "Field", new[] { $"Cannot delete field '{(string.IsNullOrWhiteSpace(field.Label) ? field.Name : field.Label)}' because a PowerFlow in another account is triggered through it." } }
                });

            if (command.Force)
            {
                var pipelinesToDeactivate = await _pipelineRepo.GetActivePipelinesReferencingFieldAsync(field, table.PublicId, ct);
                
                await _uow.BeginAsync(ct);
                try
                {
                    foreach (var pipeline in pipelinesToDeactivate)
                    {
                        pipeline.IsActive = false;
                        await _pipelineRepo.UpdateAsync(pipeline, _uow.Transaction, ct);
                    }

                    await _pipelineRepo.InvalidateStepsReferencingFieldAsync(field.Fid.Value, _uow.Transaction, ct);

                    var affected = await _fieldRepo.DeleteAsync(field.PublicId, table.Id, ct, _uow.Transaction);
                    if (affected == 0)
                        throw new NotFoundException("Field", command.FieldPublicId);

                    await _uow.CommitAsync(ct);
                }
                catch
                {
                    await _uow.RollbackAsync(ct);
                    throw;
                }

                await ReferenceProxyRules.ReassignIfRemovedAsync(table.Id, [field.Id], _relRepo, _fieldRepo, ct);

                foreach (var pipeline in pipelinesToDeactivate)
                {
                    await _auditRepo.LogActivityAsync(
                        AuditActions.SchemaChanged, AuditEntityTypes.AppField, pipeline.PublicId.ToString(), $"PowerFlow '{pipeline.Name}' automatically deactivated due to deletion of referenced field: {field.Name}", appId: table.AppId, ct: ct);
                }

                await _auditRepo.LogActivityAsync(
                    AuditActions.SchemaChanged, AuditEntityTypes.AppField, field.PublicId.ToString(), $"Field deleted: {field.Name} From TableName : {table.Name}", appId: table.AppId, ct: ct);
                await _refIndexer.RemoveSourceAsync(FieldReferenceSourceTypes.Field, field.PublicId, ct);
                await PowerBase.Application.Search.FieldDeleteIndexCleanup.PublishAsync(_publisher, indexCleanup, ct);

                return;
            }
            else
            {
                var activeReferences = await _pipelineRepo.GetActivePipelineReferencesForFieldAsync(field, table.PublicId, ct);
                if (activeReferences.Any())
                {
                    var refDetails = string.Join("; ", activeReferences.Select(r => $"PowerFlow '{r.PipelineName}' (Step: '{r.StepLabel}')"));
                    throw new ValidationException(new Dictionary<string, string[]>
                    {
                        { "Field", new[] { $"Cannot delete field '{(string.IsNullOrWhiteSpace(field.Label) ? field.Name : field.Label)}' because it is referenced in the following PowerFlows: {refDetails}" } }
                    });
                }
            }
        }

        var normalAffected = await _fieldRepo.DeleteAsync(field.PublicId, table.Id, ct);
        if (normalAffected == 0)
            throw new NotFoundException("Field", command.FieldPublicId);

        // A deleted reference proxy hands its role to the relationship's next lookup (or clears it).
        await ReferenceProxyRules.ReassignIfRemovedAsync(table.Id, [field.Id], _relRepo, _fieldRepo, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.SchemaChanged, AuditEntityTypes.AppField, field.PublicId.ToString(), $"Field deleted: {field.Name} From TableName : {table.Name}", appId: table.AppId, ct: ct);
        await _refIndexer.RemoveSourceAsync(FieldReferenceSourceTypes.Field, field.PublicId, ct);
        await PowerBase.Application.Search.FieldDeleteIndexCleanup.PublishAsync(_publisher, indexCleanup, ct);
    }
}
