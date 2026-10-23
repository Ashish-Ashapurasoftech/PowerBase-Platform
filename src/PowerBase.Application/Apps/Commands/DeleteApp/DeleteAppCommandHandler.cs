using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;

namespace PowerBase.Application.Apps.Commands.DeleteApp;

public class DeleteAppCommandHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly IAzureSearchService? _searchService;
    private readonly IQueryContext? _queryContext;

    public DeleteAppCommandHandler(
        IAppRepository appRepo,
        IAuditRepository auditRepo,
        IAzureSearchService? searchService = null,
        IQueryContext? queryContext = null)
    {
        _appRepo = appRepo;
        _auditRepo = auditRepo;
        _searchService = searchService;
        _queryContext = queryContext;
    }

    public async Task HandleAsync(DeleteAppCommand command, CancellationToken ct = default)
    {
        var app = await _appRepo.GetByPublicIdAsync(command.PublicId, ct);
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
