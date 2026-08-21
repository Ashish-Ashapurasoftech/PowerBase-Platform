using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Search.Commands.BackfillSearchIndex;
public class BackfillSearchIndexCommandHandler
{
    private readonly IQueryContext _queryContext;
    private readonly IAppRepository _appRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IAzureSearchService _searchService;

    public BackfillSearchIndexCommandHandler(
        IQueryContext queryContext,
        IAppRepository appRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IAzureSearchService searchService)
    {
        _queryContext = queryContext;
        _appRepo = appRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _searchService = searchService;
    }

    public async Task HandleAsync(BackfillSearchIndexCommand request, CancellationToken cancellationToken = default)
    {
        _queryContext.SetTenantId(request.TenantId);

        int page = 1;
        while (true)
        {
            var apps = await _appRepo.ListAsync(page, 100, cancellationToken);
            if (apps.Count == 0) break;

            foreach (var app in apps)
            {
                var tables = await _tableRepo.ListByAppAsync(app.Id, cancellationToken);
                foreach (var table in tables)
                {
                    var fields = await _fieldRepo.ListByTableAsync(table.Id, cancellationToken);

                    int recordPage = 1;
                    while (true)
                    {
                        var records = await _recordRepo.ListAsync(table, fields, recordPage, 500, null, null, null, cancellationToken);
                        if (records.Count == 0) break;

                        foreach (var record in records)
                        {
                            var publicIdStr = record["PublicId"]?.ToString();
                            if (Guid.TryParse(publicIdStr, out var publicId))
                            {
                                // The search service expects keys to be field IDs.
                                // We'll extract only the dynamic field columns (which have numeric keys in dictionary).
                                var searchableValues = new Dictionary<long, object?>();
                                foreach (var field in fields)
                                {
                                    if (field.Fid.HasValue && record.TryGetValue(field.PhysicalColumnName!, out var val))
                                    {
                                        searchableValues[field.Fid.Value] = val;
                                    }
                                }
                                
                                await _searchService.IndexRecordAsync(request.TenantId, app.Id, table.Id, publicId, searchableValues, cancellationToken);
                            }
                        }
                        recordPage++;
                    }
                }
            }
            page++;
        }
    }
}
