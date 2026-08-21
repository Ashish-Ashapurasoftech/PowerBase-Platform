using System.Linq;
using PowerBase.Application.Common.Interfaces;
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

    public DeleteFieldCommandHandler(
        IAppTableRepository tableRepo, 
        IAppFieldRepository fieldRepo, 
        IPipelineRepository pipelineRepo,
        IAuditRepository auditRepo,
        ITenantUnitOfWork uow)
    {
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

        // Dependency Check: Pipeline Field Lock
        if (field.Fid.HasValue)
        {
            if (command.Force)
            {
                var pipelinesToDeactivate = await _pipelineRepo.GetActivePipelinesReferencingFieldAsync(field.Fid.Value, ct);
                
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

                foreach (var pipeline in pipelinesToDeactivate)
                {
                    await _auditRepo.LogActivityAsync(
                        AuditActions.SchemaChanged, AuditEntityTypes.AppField, pipeline.PublicId.ToString(), $"PowerFlow '{pipeline.Name}' automatically deactivated due to deletion of referenced field: {field.Name}", appId: table.AppId, ct: ct);
                }

                await _auditRepo.LogActivityAsync(
                    AuditActions.SchemaChanged, AuditEntityTypes.AppField, field.PublicId.ToString(), $"Field deleted: {field.Name} From TableName : {table.Name}", appId: table.AppId, ct: ct);

                return;
            }
            else
            {
                var activeReferences = await _pipelineRepo.GetActivePipelineReferencesForFieldAsync(field.Fid.Value, ct);
                if (activeReferences.Any())
                {
                    var refDetails = string.Join("; ", activeReferences.Select(r => $"PowerFlow '{r.PipelineName}' (Step: '{r.StepLabel}')"));
                    throw new ValidationException(new Dictionary<string, string[]>
                    {
                        { "Field", new[] { $"Cannot delete field '{field.Name}' because it is referenced in the following active PowerFlows: {refDetails}" } }
                    });
                }
            }
        }

        var normalAffected = await _fieldRepo.DeleteAsync(field.PublicId, table.Id, ct);
        if (normalAffected == 0)
            throw new NotFoundException("Field", command.FieldPublicId);

        await _auditRepo.LogActivityAsync(
            AuditActions.SchemaChanged, AuditEntityTypes.AppField, field.PublicId.ToString(), $"Field deleted: {field.Name} From TableName : {table.Name}", appId: table.AppId, ct: ct);
    }
}
