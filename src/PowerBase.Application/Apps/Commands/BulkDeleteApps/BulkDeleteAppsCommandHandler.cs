using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Commands.BulkDeleteApps;

public class BulkDeleteAppsCommandHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly IAzureSearchService? _searchService;
    private readonly IQueryContext? _queryContext;

    public BulkDeleteAppsCommandHandler(
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

    public async Task HandleAsync(BulkDeleteAppsCommand command, CancellationToken ct = default)
    {
        if (command.PublicIds.Count == 0)
            throw new ValidationException(new Dictionary<string, string[]> { ["ids"] = ["At least one app ID is required."] });
        if (command.PublicIds.Count > 500)
            throw new ValidationException(new Dictionary<string, string[]> { ["ids"] = ["Cannot delete more than 500 apps at once."] });

        foreach (var publicId in command.PublicIds)
        {
            var app = await _appRepo.GetByPublicIdAsync(publicId, ct);
            await _appRepo.DeleteAsync(publicId, ct);

            // Purge app documents from search index
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

            await _auditRepo.LogActivityAsync(AuditActions.Deleted, AuditEntityTypes.App, publicId.ToString(), $"Application deleted: {app.Name}", ct: ct);
        }
    }
}
