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
    private readonly IMainPipelineQueueRepository? _queueRepo;

    public DeleteAppCommandHandler(
        IAppRepository appRepo,
        IAuditRepository auditRepo,
        IAzureSearchService? searchService = null,
        IQueryContext? queryContext = null,
        IPipelineRepository? pipelineRepo = null,
        IMainPipelineQueueRepository? queueRepo = null)
    {
        _pipelineRepo = pipelineRepo;
        _queueRepo = queueRepo;
        _appRepo = appRepo;
        _auditRepo = auditRepo;
        _searchService = searchService;
        _queryContext = queryContext;
    }

    public async Task HandleAsync(DeleteAppCommand command, CancellationToken ct = default)
    {
        var app = await _appRepo.GetByPublicIdAsync(command.PublicId, ct);
        await _appRepo.DeleteAsync(command.PublicId, ct);

        // Cascade: every PowerFlow owned by the deleted app is deleted too.
        if (_pipelineRepo != null)
            await DeleteAppPipelinesAsync(app.Id, ct);

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

    private async Task DeleteAppPipelinesAsync(long appId, CancellationToken ct)
    {
        var owned = await _pipelineRepo!.ListNamesByAppIdAsync(appId, ct);
        if (owned.Count == 0)
            return;

        var pipelines = new List<PowerBase.Domain.Entities.Pipeline>();
        foreach (var (publicId, _) in owned)
        {
            try { pipelines.Add(await _pipelineRepo.GetByPublicIdAsync(publicId, ct)); }
            catch (PowerBase.Domain.Exceptions.NotFoundException) { /* deleted concurrently */ }
        }

        await _pipelineRepo.SoftDeleteManyAsync(owned.Select(p => p.PublicId).ToList(), ct);

        if (_queueRepo != null && _queryContext != null && pipelines.Count > 0)
        {
            try
            {
                await _queueRepo.CancelPendingJobsForPipelinesAsync(
                    _queryContext.TenantId, pipelines.Select(p => p.Id).ToList(), "App deleted", ct);
            }
            catch
            {
                // Best-effort; queue workers also skip deleted pipelines
            }
        }

        foreach (var pipeline in pipelines)
        {
            try
            {
                var schedule = await _pipelineRepo.GetScheduleByPipelineIdAsync(pipeline.Id, ct);
                if (schedule != null)
                    await _pipelineRepo.DeleteScheduleAsync(schedule.PublicId, ct);
            }
            catch (PowerBase.Domain.Exceptions.NotFoundException) { }

            await _auditRepo.LogActivityAsync(
                AuditActions.Deleted,
                AuditEntityTypes.Pipeline,
                pipeline.PublicId.ToString(),
                $"PowerFlow workflow deleted (app deleted): {pipeline.Name}",
                appId: pipeline.AppId,
                ct: ct);
        }
    }
}
