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
    private readonly IAzureSearchService? _searchService;
    private readonly IQueryContext? _queryContext;

    public DeleteTableCommandHandler(
        IAppTableRepository tableRepo, 
        IAppFieldRepository fieldRepo,
        IPipelineRepository pipelineRepo,
        IAuditRepository auditRepo,
        IRelationshipRepository relRepo,
        DeleteRelationshipCommandHandler deleteRelationshipHandler,
        IAzureSearchService? searchService = null,
        IQueryContext? queryContext = null)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _pipelineRepo = pipelineRepo;
        _auditRepo = auditRepo;
        _relRepo = relRepo;
        _deleteRelationshipHandler = deleteRelationshipHandler;
        _searchService = searchService;
        _queryContext = queryContext;
    }

    public async Task HandleAsync(DeleteTableCommand command, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(command.PublicId, ct);

        // Fetch all fields to check for pipeline dependencies (active or inactive)
        var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);
        // Any PowerFlow step (active or inactive) that names the table (trigger, search, create, copy records…) blocks the delete, whether or
        // not the step lists individual fields. The field-level pass only adds which fields (by label) each step uses.
        var tableSteps = await _pipelineRepo.GetActivePipelineStepsUsingTableAsync(table.PublicId, ct);
        if (tableSteps.Count > 0)
        {
            var fieldsByFid = fields.Where(f => f.Fid.HasValue).GroupBy(f => f.Fid!.Value).ToDictionary(g => g.Key, g => g.First());
            var fieldRefs = await _pipelineRepo.GetActivePipelineReferencesForTableFieldsAsync(fields.Where(f => f.Fid.HasValue).ToList(), table.PublicId, ct);

            var details = tableSteps
                .GroupBy(s => s.PipelineName)
                .Select(g =>
                {
                    var stepLines = g.Select(s => s.StepLabel).Distinct().Select(stepLabel =>
                    {
                        var labels = fieldRefs
                            .Where(r => r.PipelineName == g.Key && r.StepLabel == stepLabel)
                            .Select(r => r.Fid).Distinct()
                            .Select(fid => fieldsByFid[fid])
                            .Select(f => string.IsNullOrWhiteSpace(f.Label) ? f.Name : f.Label)
                            .Distinct()
                            .ToList();
                        return labels.Count > 0 ? $"'{stepLabel}' (fields: {string.Join(", ", labels)})" : $"'{stepLabel}'";
                    });
                    return $"PowerFlow '{g.Key}' (Step: {string.Join(", ", stepLines)})";
                });
            throw new ValidationException(new Dictionary<string, string[]>
            {
                { "Table", new[] { $"Cannot delete table '{table.Name}' because it is used in PowerFlows. {string.Join("; ", details)}" } }
            });
        }

        if (await _pipelineRepo.GetCrossAccountTriggerCountAsync(table.PublicId, null, ct) > 0)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                { "Table", new[] { $"Cannot delete table '{table.Name}' because a PowerFlow in another account is triggered by it." } }
            });

        // Fetch and force-delete all relationships involving this table to clean up orphaned fields
        var relationships = await _relRepo.ListByTableAsync(table.Id, ct);

        // Deleting a relationship also removes lookup / summary fields on the OTHER table — check every relationship
        // before removing any, so a PowerFlow using one of those fields never leaves the delete half done.
        foreach (var rel in relationships)
            await _deleteRelationshipHandler.EnsurePipelinesDoNotUseAsync(rel, table.Id, ct);

        foreach (var rel in relationships)
        {
            // The deleted table's own columns go with it; but when it is the PARENT, the child table lives on, so its
            // reference field is rebuilt as text rather than left as meaningless Record IDs (best effort — never blocks).
            var restore = rel.ChildTableId == table.Id ? OriginalFieldRestore.None : OriginalFieldRestore.BestEffort;
            await _deleteRelationshipHandler.HandleAsync(new DeleteRelationshipCommand(rel.PublicId, Force: true, Restore: restore), ct);
        }

        await _tableRepo.DeleteAsync(command.PublicId, ct);

        // D13: Purge table documents from search index
        if (_searchService != null && _searchService.IsEnabled && _queryContext != null)
        {
            try
            {
                await _searchService.PurgeTableRecordsAsync(_queryContext.TenantId, table.Id, ct);
            }
            catch
            {
                // Best-effort cleanup; table deletion in SQL must not be blocked
            }
        }

        await _auditRepo.LogActivityAsync(AuditActions.Deleted, AuditEntityTypes.AppTable, command.PublicId.ToString(), $"Table deleted: {table.Name}", appId: table.AppId, ct: ct);
    }
}
