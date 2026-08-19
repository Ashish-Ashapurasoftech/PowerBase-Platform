using PowerBase.Application.Common.Interfaces;
using System.Collections.Concurrent;

namespace PowerBase.Application.Records.Queries.SearchGlobalRecords;

public class SearchGlobalRecordsQueryHandler
{
    private readonly IAzureSearchService _searchService;
    private readonly IQueryContext _queryContext;
    private readonly IAppRepository _appRepo;
    private readonly IAppTableRepository _tableRepo;

    public SearchGlobalRecordsQueryHandler(
        IAzureSearchService searchService, 
        IQueryContext queryContext,
        IAppRepository appRepo,
        IAppTableRepository tableRepo)
    {
        _searchService = searchService;
        _queryContext = queryContext;
        _appRepo = appRepo;
        _tableRepo = tableRepo;
    }

    public async Task<SearchGlobalRecordsResult> HandleAsync(SearchGlobalRecordsQuery request, CancellationToken cancellationToken = default)
    {
        var rawResults = await _searchService.SearchGlobalAsync(_queryContext.TenantId, request.SearchText, request.AppId, cancellationToken);
        if (rawResults.Count == 0) return new SearchGlobalRecordsResult([]);

        var appCache = new ConcurrentDictionary<long, Domain.Entities.App>();
        var tableCache = new ConcurrentDictionary<long, Domain.Entities.AppTable>();
        
        var finalResults = new List<SearchGlobalRecordsResultItem>();

        foreach (var r in rawResults)
        {
            if (!tableCache.TryGetValue(r.TableId, out var table))
            {
                try
                {
                    table = await _tableRepo.GetByIdAsync(r.TableId, cancellationToken);
                    tableCache[r.TableId] = table;
                }
                catch { continue; } // Deleted table
            }

            if (!appCache.TryGetValue(r.AppId, out var app))
            {
                try
                {
                    app = await _appRepo.GetByIdAsync(r.AppId, cancellationToken);
                    appCache[r.AppId] = app;
                }
                catch { continue; } // Deleted app
            }

            finalResults.Add(new SearchGlobalRecordsResultItem(
                r.PublicId, 
                app.PublicId, 
                app.Name, 
                table.PublicId, 
                table.SingularLabel ?? table.Name ?? "Record", 
                table.Icon));
        }

        return new SearchGlobalRecordsResult(finalResults);
    }
}
