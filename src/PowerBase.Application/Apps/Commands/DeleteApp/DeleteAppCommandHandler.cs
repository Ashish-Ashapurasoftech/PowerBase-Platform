using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;

namespace PowerBase.Application.Apps.Commands.DeleteApp;

public class DeleteAppCommandHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly IAzureSearchService? _searchService;
    private readonly IQueryContext? _queryContext;
    private readonly IPipelineRepository? _pipelineRepo;
    private readonly PowerBase.Application.Pipelines.Commands.DeletePipeline.DeletePipelineCommandHandler? _deletePipeline;

    public DeleteAppCommandHandler(
        IAppRepository appRepo,
        IAuditRepository auditRepo,
        IAzureSearchService? searchService = null,
        IQueryContext? queryContext = null,
        IPipelineRepository? pipelineRepo = null,
        PowerBase.Application.Pipelines.Commands.DeletePipeline.DeletePipelineCommandHandler? deletePipeline = null)
    {
        _pipelineRepo = pipelineRepo;
        _deletePipeline = deletePipeline;
        _appRepo = appRepo;
        _auditRepo = auditRepo;
        _searchService = searchService;
        _queryContext = queryContext;
    }

    public async Task HandleAsync(DeleteAppCommand command, CancellationToken ct = default)
    {
        var app = await _appRepo.GetByPublicIdAsync(command.PublicId, ct);

        // powerflowextraRd: deleting an app deletes its PowerFlows too, as Quickbase does with an app's pipelines. Each goes
        // through the PowerFlow delete (pending runs cancelled, schedule and trigger subscriptions removed, audited), before the
        // app itself, so no PowerFlow is left running on — or listed for — an app that no longer exists.
        if (_pipelineRepo != null && _deletePipeline != null)
        {
            foreach (var (pipelinePublicId, _) in await _pipelineRepo.ListNamesByAppIdAsync(app.Id, ct))
                await _deletePipeline.HandleAsync(new PowerBase.Application.Pipelines.Commands.DeletePipeline.DeletePipelineCommand(pipelinePublicId), ct);
        }

        await _appRepo.DeleteAsync(command.PublicId, ct);

        // D13: Purge app documents from search index
        if (_searchService != null && _searchService.IsEnabled && _queryContext != null)
        {
            try
            {
                await _searchService.PurgeAppRecordsAsync(_queryContext.TenantId, app.Id, ct);
            }
            catch
            {
                // Best-effort cleanup; app deletion in SQL must not be blocked
            }
        }

        await _auditRepo.LogActivityAsync(AuditActions.Deleted, AuditEntityTypes.App, command.PublicId.ToString(), $"Application deleted: {app.Name}", ct: ct);
    }
}
