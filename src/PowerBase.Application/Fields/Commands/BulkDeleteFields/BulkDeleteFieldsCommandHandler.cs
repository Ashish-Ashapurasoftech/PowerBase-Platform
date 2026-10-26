using System.Collections.Generic;
using System.Linq;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Application.FieldReferences;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Fields.Commands.BulkDeleteFields;

public class BulkDeleteFieldsCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IPipelineRepository _pipelineRepo;
    private readonly IAppAccessService _appAccessService;
    private readonly IAuditRepository _auditRepo;
    private readonly ITenantUnitOfWork _uow;
    private readonly IRelationshipRepository _relRepo;
    private readonly IFieldReferenceIndexer _refIndexer;
    private readonly IMessagePublisher? _publisher;
    private readonly IQueryContext? _queryContext;

    public BulkDeleteFieldsCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IPipelineRepository pipelineRepo,
        IAppAccessService appAccessService,
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
        _appAccessService = appAccessService;
        _auditRepo = auditRepo;
        _uow = uow;
    }

    public async Task HandleAsync(BulkDeleteFieldsCommand command, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);
        
        // Fetch all fields in the table to match their FIDs and system status
        var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);
        var fieldsToDelete = fields.Where(f => command.FieldPublicIds.Contains(f.PublicId) && !f.IsSystem).ToList();

        // The columns these fields used in the search index, read now: once they are deleted the table no longer says.
        var indexCleanup = _queryContext == null
            ? []
            : PowerBase.Application.Search.FieldDeleteIndexCleanup.Capture(_queryContext.TenantId, table, fields, fieldsToDelete);

        await RelationshipKeyFieldDeleteGuard.EnsureNotRelationshipKeyAsync(table, fieldsToDelete, _relRepo, ct);
        ReferenceFilterDependencyGuard.EnsureNotControlling(table, fields, fieldsToDelete);

        var blockedFieldsErrors = new Dictionary<string, string[]>();
        var pipelinesToDeactivate = new Dictionary<Guid, Pipeline>();

        var fidsToDelete = fieldsToDelete.Where(f => f.Fid.HasValue).Select(f => f.Fid!.Value).ToList();
        if (fidsToDelete.Count > 0 && await _pipelineRepo.GetCrossAccountTriggerCountAsync(table.PublicId, fidsToDelete, ct) > 0)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                { "Fields", new[] { "Cannot delete these fields because a PowerFlow in another account is triggered through one of them." } }
            });

        foreach (var field in fieldsToDelete)
        {
            if (field.Fid.HasValue)
            {
                if (command.Force)
                {
                    var refs = await _pipelineRepo.GetActivePipelinesReferencingFieldAsync(field, table.PublicId, ct);
                    foreach (var p in refs)
                    {
                        pipelinesToDeactivate[p.PublicId] = p;
                    }
                }
                else
                {
                    var activeReferences = await _pipelineRepo.GetActivePipelineReferencesForFieldAsync(field, table.PublicId, ct);
                    if (activeReferences.Any())
                    {
                        var refDetails = string.Join("; ", activeReferences.Select(r => $"PowerFlow '{r.PipelineName}' (Step: '{r.StepLabel}')"));
                        blockedFieldsErrors[field.Name] = new[] { $"Cannot delete field '{(string.IsNullOrWhiteSpace(field.Label) ? field.Name : field.Label)}' because it is referenced in the following PowerFlows: {refDetails}" };
                    }
                }
            }
        }

        if (blockedFieldsErrors.Any())
        {
            throw new ValidationException(blockedFieldsErrors);
        }

        if (command.Force)
        {
            await _uow.BeginAsync(ct);
            try
            {
                foreach (var p in pipelinesToDeactivate.Values)
                {
                    p.IsActive = false;
                    await _pipelineRepo.UpdateAsync(p, _uow.Transaction, ct);
                }

                foreach (var field in fieldsToDelete)
                {
                    if (field.Fid.HasValue)
                    {
                        await _pipelineRepo.InvalidateStepsReferencingFieldAsync(field.Fid.Value, _uow.Transaction, ct);
                    }
                }

                var deletedCount = await _fieldRepo.BulkDeleteAsync(command.FieldPublicIds, table.Id, ct, _uow.Transaction);

                await _uow.CommitAsync(ct);
            }
            catch
            {
                await _uow.RollbackAsync(ct);
                throw;
            }

            foreach (var p in pipelinesToDeactivate.Values)
            {
                await _auditRepo.LogActivityAsync(
                    AuditActions.SchemaChanged, AuditEntityTypes.AppField, p.PublicId.ToString(), $"PowerFlow '{p.Name}' automatically deactivated due to bulk deletion of referenced fields", appId: table.AppId, ct: ct);
            }

            var deletedFieldsCount = fieldsToDelete.Count;
            await _auditRepo.LogActivityAsync(
                AuditActions.SchemaChanged,
                AuditEntityTypes.AppField,
                table.PublicId.ToString(),
                $"{deletedFieldsCount} field(s) bulk-deleted from table {table.Name}",
                appId: table.AppId,
                ct: ct);
            await RemoveDeletedFieldsAsync(command.FieldPublicIds, ct);
            await PowerBase.Application.Search.FieldDeleteIndexCleanup.PublishAsync(_publisher, indexCleanup, ct);

            return;
        }

        var normalDeletedCount = await _fieldRepo.BulkDeleteAsync(command.FieldPublicIds, table.Id, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.SchemaChanged,
            AuditEntityTypes.AppField,
            table.PublicId.ToString(),
            $"{normalDeletedCount} field(s) bulk-deleted from table {table.Name}",
            appId: table.AppId,
            ct: ct);
        await RemoveDeletedFieldsAsync(command.FieldPublicIds, ct);
        await PowerBase.Application.Search.FieldDeleteIndexCleanup.PublishAsync(_publisher, indexCleanup, ct);
    }

    private async Task RemoveDeletedFieldsAsync(IEnumerable<Guid> fieldPublicIds, CancellationToken ct)
    {
        foreach (var id in fieldPublicIds)
            await _refIndexer.RemoveSourceAsync(FieldReferenceSourceTypes.Field, id, ct);
    }
}
