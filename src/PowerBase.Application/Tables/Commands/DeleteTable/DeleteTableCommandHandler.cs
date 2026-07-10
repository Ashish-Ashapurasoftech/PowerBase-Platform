using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Application.Relationships.Commands.DeleteRelationship;

namespace PowerBase.Application.Tables.Commands.DeleteTable;

public class DeleteTableCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly IRelationshipRepository _relRepo;
    private readonly DeleteRelationshipCommandHandler _deleteRelationshipHandler;

    public DeleteTableCommandHandler(
        IAppTableRepository tableRepo, 
        IAuditRepository auditRepo,
        IRelationshipRepository relRepo,
        DeleteRelationshipCommandHandler deleteRelationshipHandler)
    {
        _tableRepo = tableRepo;
        _auditRepo = auditRepo;
        _relRepo = relRepo;
        _deleteRelationshipHandler = deleteRelationshipHandler;
    }

    public async Task HandleAsync(DeleteTableCommand command, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(command.PublicId, ct);

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
