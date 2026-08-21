using System.Collections.Generic;
using System.Linq;
using PowerBase.Application.Common.Interfaces;
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

    public BulkDeleteFieldsCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IPipelineRepository pipelineRepo,
        IAppAccessService appAccessService,
        IAuditRepository auditRepo,
        ITenantUnitOfWork uow)
    {
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

        var blockedFieldsErrors = new Dictionary<string, string[]>();
        var pipelinesToDeactivate = new Dictionary<Guid, Pipeline>();

        foreach (var field in fieldsToDelete)
        {
            if (field.Fid.HasValue)
            {
                if (command.Force)
                {
                    var refs = await _pipelineRepo.GetActivePipelinesReferencingFieldAsync(field.Fid.Value, ct);
                    foreach (var p in refs)
                    {
                        pipelinesToDeactivate[p.PublicId] = p;
                    }
                }
                else
                {
                    var activeReferences = await _pipelineRepo.GetActivePipelineReferencesForFieldAsync(field.Fid.Value, ct);
                    if (activeReferences.Any())
                    {
                        var refDetails = string.Join("; ", activeReferences.Select(r => $"PowerFlow '{r.PipelineName}' (Step: '{r.StepLabel}')"));
                        blockedFieldsErrors[field.Name] = new[] { $"Cannot delete field '{field.Name}' because it is referenced in the following active PowerFlows: {refDetails}" };
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
    }
}
