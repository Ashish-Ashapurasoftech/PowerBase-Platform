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
                    if (request.TableId.HasValue && table.Id != request.TableId.Value)
                    {
                        continue;
                    }

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

                            // D24: Terminate paging when table has no more records
                            if (records == null || records.Count == 0)
                            {
                                break;
                            }

                            var isWide = PowerBase.Application.Search.SearchSlotResolver.IsWideTable(fields);
                            var targetIndex = isWide ? $"pb-tbl-{table.Id}" : null;
                            var slotMap = PowerBase.Application.Search.SearchSlotResolver.BuildSlotMap(fields);
                            var docsToBatch = new List<SearchIndexDocument>();

                            foreach (var record in records)
                            {
                                var publicIdStr = record["PublicId"]?.ToString();
                                if (Guid.TryParse(publicIdStr, out var publicId))
                                {
                                    var searchableValues = new Dictionary<long, object?>();
                                    var slotValues = new Dictionary<string, object?>();

                                    foreach (var field in fields)
                                    {
                                        var colName = PowerBase.Domain.Constants.PhysicalNaming.GetPhysicalColumnName(field);
                                        if (field.Fid.HasValue && record.TryGetValue(colName, out var val))
                                        {
                                            searchableValues[field.Fid.Value] = val;
                                            if (slotMap.TryGetValue(field.Fid.Value, out var slotName))
                                            {
                                                if (!isWide && slotName.StartsWith("f_"))
                                                {
                                                    continue;
                                                }

                                                if (PowerBase.Application.Search.SearchSlotResolver.IsNumeric(field.TypeCode))
                                                    slotValues[slotName] = PowerBase.Application.Search.SearchSlotResolver.ParseDouble(val);
                                                else if (PowerBase.Application.Search.SearchSlotResolver.IsDate(field.TypeCode))
                                                    slotValues[slotName] = PowerBase.Application.Search.SearchSlotResolver.ParseDateTimeOffset(val);
                                                else if (PowerBase.Application.Search.SearchSlotResolver.IsBoolean(field.TypeCode))
                                                    slotValues[slotName] = PowerBase.Application.Search.SearchSlotResolver.ParseBoolean(val);
                                                else
                                                    slotValues[slotName] = PowerBase.Application.Search.SearchSlotResolver.ParseString(val);
                                            }
                                        }
                                    }

                                    docsToBatch.Add(new SearchIndexDocument(request.TenantId, app.Id, table.Id, publicId, searchableValues, slotValues, targetIndex));
                                }
                                else
                                {
                                    _logger.LogWarning("Record missing or invalid PublicId in Table {TableName} (ID: {TableId}).", table.Name, table.Id);
                                }
                            }

                            string? batchError = null;
                            if (docsToBatch.Count > 0)
                            {
                                try
                                {
                                    await _searchService.BulkIndexRecordsAsync(docsToBatch, cancellationToken);
                                    totalRecordsIndexed += docsToBatch.Count;
                                }
                                catch (Exception ex)
                                {
                                    failedRecordsCount += docsToBatch.Count;
                                    batchError = ex.Message;
                                    _logger.LogError(ex, "Failed to bulk index {Count} records for Table {TableName} (ID: {TableId}).", docsToBatch.Count, table.Name, table.Id);
                                }
                            }

                            request.OnProgress?.Invoke(totalApps, totalTables, totalRecordsIndexed, failedRecordsCount, batchError);

                            if (records.Count < 500)
                            {
                                break;
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
