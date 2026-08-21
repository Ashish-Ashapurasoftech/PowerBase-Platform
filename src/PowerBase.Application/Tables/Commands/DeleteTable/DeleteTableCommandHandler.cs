using System.Collections.Generic;
using System.Linq;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Application.Relationships.Commands.DeleteRelationship;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Tables.Commands.DeleteTable;

public class DeleteTableCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IPipelineRepository _pipelineRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly IRelationshipRepository _relRepo;
    private readonly DeleteRelationshipCommandHandler _deleteRelationshipHandler;

    public DeleteTableCommandHandler(
        IAppTableRepository tableRepo, 
        IAppFieldRepository fieldRepo,
        IPipelineRepository pipelineRepo,
        IAuditRepository auditRepo,
        IRelationshipRepository relRepo,
        DeleteRelationshipCommandHandler deleteRelationshipHandler)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _pipelineRepo = pipelineRepo;
        _auditRepo = auditRepo;
        _relRepo = relRepo;
        _deleteRelationshipHandler = deleteRelationshipHandler;
    }

    public async Task HandleAsync(DeleteTableCommand command, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(command.PublicId, ct);

        // Fetch all fields to check for active pipeline dependencies
        var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);
        var blockedFieldsErrors = new Dictionary<string, string[]>();
        foreach (var field in fields)
        {
            if (field.Fid.HasValue)
            {
                var activeReferences = await _pipelineRepo.GetActivePipelineReferencesForFieldAsync(field.Fid.Value, ct);
                if (activeReferences.Any())
                {
                    var refDetails = string.Join("; ", activeReferences.Select(r => $"PowerFlow '{r.PipelineName}' (Step: '{r.StepLabel}')"));
                    blockedFieldsErrors[field.Name] = new[] { $"Cannot delete table because field '{field.Name}' is referenced in active PowerFlows: {refDetails}" };
                }
            }
        }

        if (blockedFieldsErrors.Any())
        {
            throw new ValidationException(blockedFieldsErrors);
        }

        // Fetch and force-delete all relationships involving this table to clean up orphaned fields
        var relationships = await _relRepo.ListByTableAsync(table.Id, ct);
        foreach (var rel in relationships)
        {
            await _deleteRelationshipHandler.HandleAsync(new DeleteRelationshipCommand(rel.PublicId, Force: true), ct);
        }

        await _tableRepo.DeleteAsync(command.PublicId, ct);
        await _auditRepo.LogActivityAsync(AuditActions.Deleted, AuditEntityTypes.AppTable, command.PublicId.ToString(), $"Table deleted: {table.Name}", appId: table.AppId, ct: ct);
    }
}
