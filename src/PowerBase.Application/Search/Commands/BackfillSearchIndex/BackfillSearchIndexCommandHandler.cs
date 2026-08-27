using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<BackfillSearchIndexCommandHandler> _logger;

    public BackfillSearchIndexCommandHandler(
        IQueryContext queryContext,
        IAppRepository appRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IAzureSearchService searchService,
        ILogger<BackfillSearchIndexCommandHandler> logger)
    {
        _queryContext = queryContext;
        _appRepo = appRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _searchService = searchService;
        _logger = logger;
    }

    public async Task HandleAsync(BackfillSearchIndexCommand request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting search index backfill for Tenant {TenantId}.", request.TenantId);
        _queryContext.SetTenantId(request.TenantId);

        int page = 1;
        int totalApps = 0;
        int totalTables = 0;
        int totalRecordsIndexed = 0;
        int failedRecordsCount = 0;

        while (true)
        {
            var apps = await _appRepo.ListAsync(page, 100, cancellationToken);
            if (apps.Count == 0) break;

            foreach (var app in apps)
            {
                totalApps++;
                var tables = await _tableRepo.ListByAppAsync(app.Id, cancellationToken);
                foreach (var table in tables)
                {
                    totalTables++;
                    _logger.LogInformation("Processing Table: {TableName} (ID: {TableId}, Physical: {PhysicalTable}) in App: {AppName} (ID: {AppId}).", 
                        table.Name, table.Id, table.PhysicalTableName, app.Name, app.Id);

                    try
                    {
                        var fields = await _fieldRepo.ListByTableAsync(table.Id, cancellationToken);

                        int recordPage = 1;
                        while (true)
                        {
                            IReadOnlyList<IReadOnlyDictionary<string, object?>> records;
                            try
                            {
                                records = await _recordRepo.ListAsync(table, fields, recordPage, 500, null, null, null, cancellationToken);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to list records for Table {TableName} (ID: {TableId}) at page {RecordPage}.", table.Name, table.Id, recordPage);
                                break; // Break out of this table's record paging
                            }

                            if (records.Count == 0) break;

                            foreach (var record in records)
                            {
                                var publicIdStr = record["PublicId"]?.ToString();
                                if (Guid.TryParse(publicIdStr, out var publicId))
                                {
                                    try
                                    {
                                        // The search service expects keys to be field IDs.
                                        // We'll extract only the dynamic field columns (which have numeric keys in dictionary).
                                        var searchableValues = new Dictionary<long, object?>();
                                        foreach (var field in fields)
                                        {
                                            var colName = PowerBase.Domain.Constants.PhysicalNaming.GetPhysicalColumnName(field);
                                            if (field.Fid.HasValue && record.TryGetValue(colName, out var val))
                                            {
                                                searchableValues[field.Fid.Value] = val;
                                            }
                                        }

                                        await _searchService.IndexRecordAsync(request.TenantId, app.Id, table.Id, publicId, searchableValues, cancellationToken);
                                        totalRecordsIndexed++;
                                    }
                                    catch (Exception ex)
                                    {
                                        failedRecordsCount++;
                                        _logger.LogError(ex, "Failed to index record {RecordPublicId} for Table {TableName} (ID: {TableId}).", publicId, table.Name, table.Id);
                                    }
                                }
                                else
                                {
                                    _logger.LogWarning("Record missing or invalid PublicId in Table {TableName} (ID: {TableId}).", table.Name, table.Id);
                                }
                            }
                            recordPage++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Unhandled error processing Table {TableName} (ID: {TableId}). Continuing with other tables.", table.Name, table.Id);
                    }
                }
            }
            page++;
        }

        _logger.LogInformation("Completed search index backfill for Tenant {TenantId}. Apps: {AppsCount}, Tables: {TablesCount}, Indexed Records: {IndexedCount}, Failed Records: {FailedCount}.", 
            request.TenantId, totalApps, totalTables, totalRecordsIndexed, failedRecordsCount);
    }
}
