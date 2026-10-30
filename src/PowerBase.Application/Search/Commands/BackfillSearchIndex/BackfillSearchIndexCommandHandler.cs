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
                        // Same slots, same reads and same document builder as the indexer worker, so a backfill can never write a layout
                        // the queries do not read. A table that has outgrown the shared index is copied to its own index too, then switched.
                        await _fieldRepo.EnsureSearchSlotsAsync(table.Id, cancellationToken);
                        var fields = await _fieldRepo.ListByTableAsync(table.Id, cancellationToken);
                        var info = SearchTableSlotInfo.From(table.Id, fields);
                        var copiedAllPages = true;

                        // Columns still held by deleted fields (deleted before their columns were freed after cleanup): emptied in every
                        // record on the way, and given back to the pool once every page is in, so they are not reserved forever.
                        var deletedHolders = info.IsWide ? [] : await _fieldRepo.ListDeletedSlotHoldersAsync(table.Id, cancellationToken);
                        var staleColumns = deletedHolders
                            .SelectMany(f => new[] { f.SearchSlot, f.SearchSlotEnd })
                            .Where(s => !string.IsNullOrWhiteSpace(s))
                            .Select(s => s!)
                            .ToList();

                        // Paged by record id: deleting records during the run cannot shift others into a page already done.
                        long afterId = 0;
                        while (true)
                        {
                            IReadOnlyList<SearchIndexDocument> batch;
                            try
                            {
                                (batch, afterId) = await _recordRepo.GetTableIndexPageAsync(request.TenantId, table.Id, afterId, SearchWideMigration.PageSize, cancellationToken);
                            }
                            catch (Exception ex)
                            {
                                copiedAllPages = false;
                                _logger.LogError(ex, "Failed to read records for Table {TableName} (ID: {TableId}) after record {AfterId}.", table.Name, table.Id, afterId);
                                break; // Break out of this table's record paging
                            }

                            // D24: Terminate paging when table has no more records
                            if (batch.Count == 0)
                            {
                                break;
                            }

                            // Every record is read in full, so the free-text field is rebuilt (and emptied of text that is gone).
                            var docsToBatch = SearchIndexDocumentBuilder.ClearColumns(batch
                                .SelectMany(d => SearchIndexDocumentBuilder.Build(info, d.TenantId, d.AppId, d.TableId, d.PublicId, d.Values, null, complete: true))
                                .ToList(), info.TargetIndex, staleColumns);

                            string? batchError = null;
                            try
                            {
                                var rejected = await _searchService.BulkIndexRecordsWithResultsAsync(docsToBatch, cancellationToken);
                                var rejectedRecords = rejected.Select(f => f.Key).Distinct().Count();
                                failedRecordsCount += rejectedRecords;
                                totalRecordsIndexed += batch.Count - Math.Min(rejectedRecords, batch.Count);
                                if (rejectedRecords > 0)
                                {
                                    copiedAllPages = false;
                                    batchError = $"{rejectedRecords} record(s) were rejected by the search service.";
                                    foreach (var f in rejected)
                                        _logger.LogError("Backfill: document {RecordPublicId} in Table {TableId} was not indexed: status {Status}, error {Error}", f.Key, table.Id, f.Status, f.Error);
                                }
                            }
                            catch (Exception ex)
                            {
                                copiedAllPages = false;
                                failedRecordsCount += batch.Count;
                                batchError = ex.Message;
                                _logger.LogError(ex, "Failed to bulk index {Count} records for Table {TableName} (ID: {TableId}).", batch.Count, table.Name, table.Id);
                            }

                            request.OnProgress?.Invoke(totalApps, totalTables, totalRecordsIndexed, failedRecordsCount, batchError);

                            if (batch.Count < SearchWideMigration.PageSize)
                            {
                                break;
                            }
                        }

                        if (copiedAllPages)
                            foreach (var holder in deletedHolders)
                                await _fieldRepo.ReleaseDeletedFieldSlotAsync(table.Id, holder.Fid!.Value, holder.SearchSlot!, cancellationToken);

                        // Only after every page is in the table's own index: switch queries to it and drop the old documents.
                        if (info.MigratingToWide && copiedAllPages)
                            await SearchWideMigration.FinalizeAsync(_fieldRepo, _searchService, request.TenantId, table.Id, cancellationToken);
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
