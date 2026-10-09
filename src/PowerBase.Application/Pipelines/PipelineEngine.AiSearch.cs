using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pipelines;

/// <summary>
/// Search Records answered by Azure AI Search where the index can answer it exactly (see <see cref="PipelineAiSearchPlanner"/>),
/// and by SQL everywhere else. AI Search only finds <em>candidate</em> record ids, with no ceiling on how many (paged
/// 1,000 at a time); every candidate is then read from SQL with the complete filter applied, so a deleted, changed or
/// not-yet-reindexed record can never be returned. When AI Search finds nothing, or fails before anything was staged,
/// the search is simply run in SQL.
/// </summary>
public partial class PipelineEngine
{
    /// <summary>SQL Server accepts at most 2,100 parameters per query. Candidate public ids are looked up (a bare "IN") in chunks of
    /// this size — chunked, never trimmed.</summary>
    public const int AiCandidatePublicIdChunkSize = 2000;

    /// <summary>Candidate record ids sent with the search's own filter, in chunks of this size. Every id and every value of the
    /// filter is a parameter, so this leaves room for the filter (the planner caps it at <see cref="PipelineAiSearchPlanner.MaxInValues"/>
    /// list values and <see cref="PipelineAiSearchPlanner.MaxConditions"/> conditions) and the paging parameters under 2,100.</summary>
    public const int AiCandidateRecordIdChunkSize = 1000;

    /// <summary>The search service of the scope the current step runs in. A step that runs through a saved account reads another
    /// tenant's tables, so the index (and the tenant id it is filtered by) must be that tenant's, not the flow owner's.</summary>
    private IAzureSearchService? StepAzureSearch() =>
        CurrentStepServices.GetService(typeof(IAzureSearchService)) as IAzureSearchService ?? _azureSearchService;

    private long StepTenantId() =>
        (CurrentStepServices.GetService(typeof(IQueryContext)) as IQueryContext)?.TenantId ?? _queryContext.TenantId;

    /// <summary>Whether this search may be answered by AI Search (with the filter to use), or must be read from SQL.</summary>
    private async Task<PipelineAiSearchPlan> PlanAiSearchAsync(
        AppTable table, IReadOnlyList<AppField> fields, FilterGroup? filterTree, CancellationToken ct)
    {
        var azure = StepAzureSearch();
        if (azure == null || !azure.IsGridSearchEnabled)
            return new PipelineAiSearchPlan(null, "Azure AI Search is not enabled");

        var plan = PipelineAiSearchPlanner.Evaluate(filterTree, fields);
        if (!plan.UseAiSearch)
        {
            _logger.LogInformation("[Pipeline SearchRecords] Table {TableId} is searched in SQL, not Azure AI Search: {Reason}.", table.Id, plan.SqlReason);
            return plan;
        }
        if (!await azure.IsHealthyAsync(ct))
        {
            _logger.LogWarning("[Pipeline SearchRecords] Azure AI Search is not healthy; table {TableId} is searched in SQL.", table.Id);
            return new PipelineAiSearchPlan(null, "Azure AI Search is not healthy");
        }
        return plan;
    }

    /// <summary>
    /// The SQL search: every row matching <paramref name="tree"/> (or the first <paramref name="limit"/>), read page by page so
    /// nothing is capped. Encrypted fields hold ciphertext, and Formula / Lookup / Summary / label-Reference fields have no
    /// column, so conditions on them are split out of the SQL tree and evaluated in memory on the decrypted / projected rows.
    /// </summary>
    private async Task<(List<IReadOnlyDictionary<string, object?>> Rows, bool ComputedMerged)> SearchSqlAsync(
        AppTable table, IReadOnlyList<AppField> fields, FilterGroup? tree, int? limit,
        HashSet<long> inMemoryFilterFids, HashSet<long> computedFieldFids,
        IPipelineRecordSearchService recordSearchService, IRecordRepository recordRepo, int pageSize, CancellationToken ct)
    {
        async Task<List<IReadOnlyDictionary<string, object?>>> FetchAllAsync(FilterGroup? filter)
        {
            var all = new List<IReadOnlyDictionary<string, object?>>();
            if (recordSearchService is IKeysetPipelineRecordSearchService pager && recordSearchService.SupportsKeysetPaging)
            {
                await foreach (var pageRows in pager.SearchPagesAsync(table, fields, pageSize, filter, 0, long.MaxValue, ct))
                    all.AddRange(pageRows);
                return all;
            }
            if (recordSearchService != null)
            {
                var rows = await recordSearchService.SearchAsync(table, fields, null, filter, ct);
                return rows.ToList();
            }
            for (var page = 1; ; page++)
            {
                var pageRows = await recordRepo.ListAsync(table, fields, page, pageSize, filterTree: filter, ct: ct);
                if (pageRows.Count == 0) break;
                all.AddRange(pageRows);
                if (pageRows.Count < pageSize) break;
            }
            return all;
        }

        // Encrypted fields store ciphertext in their physical column, so a SQL LIKE/= condition against them can never
        // match. Split those conditions out of the SQL tree and evaluate them in memory against decrypted candidate rows
        // instead — mirrors RunReportQueryHandler's handling of formula (compute-on-read) fields.
        if (inMemoryFilterFids.Count > 0 && FormulaFilterSorter.TreeContainsFormulaField(tree, inMemoryFilterFids))
        {
            var (physicalFilterTree, inMemoryFilterTree) = FormulaFilterSorter.SplitFilterTree(tree, inMemoryFilterFids);
            // Fetch EVERY physical-filter-matching candidate via pagination — no fixed cap — so nothing is silently
            // dropped before the in-memory conditions are evaluated below, no matter how many rows match physically.
            var candidates = await FetchAllAsync(physicalFilterTree);
            var (computedPerRow, rowsNarrowed) = await ProjectForFilterAsync(table, fields, candidates, PipelineComputedProjection.ReferencedFids(inMemoryFilterTree), ct);
            var pairs = candidates
                .Select((r, i) => (Row: r, Computed: computedPerRow != null ? computedPerRow[i] : (IReadOnlyDictionary<long, object?>)EmptyComputedValues))
                .ToList();
            if (inMemoryFilterTree != null)
                pairs = PipelineComputedFilter.Apply(pairs, inMemoryFilterTree, fields);
            // The Reference display text was projected only to evaluate the filter: a table with no
            // computed fields keeps returning the stored Record ID# in the step output.
            IEnumerable<IReadOnlyDictionary<string, object?>> filtered = pairs.Select(p =>
                computedFieldFids.Count == 0 || rowsNarrowed ? p.Row : MergeComputed(p.Row, p.Computed));
            // No projection ran when the filter read only stored fields: the matched rows get theirs from the caller.
            return ((limit.HasValue ? filtered.Take(limit.Value) : filtered).ToList(), computedPerRow != null && !rowsNarrowed);
        }

        if (limit.HasValue)
        {
            var records = recordSearchService != null
                ? await recordSearchService.SearchAsync(table, fields, maxResults: limit, filterTree: tree, ct: ct)
                : await recordRepo.ListAsync(table, fields, page: 1, pageSize: limit.Value, filterTree: tree, ct: ct);
            return (records?.ToList() ?? new List<IReadOnlyDictionary<string, object?>>(), false);
        }

        // No user-configured MaxResults — fetch every matching row via pagination.
        return (await FetchAllAsync(tree), false);
    }

    /// <summary>The filter plus a restriction to these record ids (and, for a snapshotted search, to ids up to
    /// <paramref name="maxRecordId"/>). An OR filter is nested so the restriction applies to all of it.</summary>
    public static FilterGroup WithIdRestriction(FilterGroup? tree, IEnumerable<long> recordIds, long? maxRecordId)
    {
        var restriction = new List<FilterNode>
        {
            new() { Condition = new FilterCondition { FieldId = 3, Operator = "in", Value = JsonSerializer.Serialize(recordIds) } }
        };
        if (maxRecordId.HasValue)
            restriction.Add(new FilterNode { Condition = new FilterCondition { FieldId = 3, Operator = "lte", Value = maxRecordId.Value.ToString(CultureInfo.InvariantCulture) } });

        if (tree == null || tree.Nodes.Count == 0) return new FilterGroup { Logic = "and", Nodes = restriction };
        if (string.Equals(tree.Logic, "or", StringComparison.OrdinalIgnoreCase))
            return new FilterGroup { Logic = "and", Nodes = [.. restriction, new FilterNode { Group = tree }] };
        return new FilterGroup { Logic = "and", Nodes = [.. tree.Nodes, .. restriction] };
    }

    /// <summary>Reads the candidate records AI Search found from SQL, applying the complete filter, so only records SQL itself
    /// matches come back. Ordered by record id, like every SQL search.</summary>
    private async Task<List<IReadOnlyDictionary<string, object?>>> VerifyAiCandidatesAsync(
        AppTable table, IReadOnlyList<AppField> fields, FilterGroup? tree, IReadOnlyList<Guid> candidatePublicIds, long? maxRecordId,
        HashSet<long> inMemoryFilterFids, HashSet<long> computedFieldFids,
        IPipelineRecordSearchService recordSearchService, IRecordRepository recordRepo, int pageSize, CancellationToken ct)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var publicIdChunk in candidatePublicIds.Chunk(AiCandidatePublicIdChunkSize))
        {
            // Ids of records that are still there (a record deleted since it was indexed is gone here).
            var recordIds = await recordRepo.GetIdsByPublicIdsAsync(table, publicIdChunk, ct);
            foreach (var idChunk in recordIds.Chunk(AiCandidateRecordIdChunkSize))
            {
                var (found, _) = await SearchSqlAsync(table, fields, WithIdRestriction(tree, idChunk, maxRecordId), null,
                    inMemoryFilterFids, computedFieldFids, recordSearchService, recordRepo, pageSize, ct);
                rows.AddRange(found);
            }
        }
        return rows.OrderBy(r => r.TryGetValue("Id", out var id) && id != null ? Convert.ToInt64(id, CultureInfo.InvariantCulture) : long.MaxValue).ToList();
    }

    /// <summary>Non-streamed search through AI Search: every candidate id, verified in SQL. Stops once <paramref name="limit"/>
    /// (a user-configured MaxResults) rows are in hand. Throws if AI Search fails; the caller falls back to SQL.</summary>
    private async Task<List<IReadOnlyDictionary<string, object?>>> ReadAiSearchRowsAsync(
        AppTable table, IReadOnlyList<AppField> fields, FilterGroup? tree, string odataFilter, int? limit,
        HashSet<long> inMemoryFilterFids, HashSet<long> computedFieldFids,
        IPipelineRecordSearchService recordSearchService, IRecordRepository recordRepo, int pageSize, CancellationToken ct)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await foreach (var page in StepAzureSearch()!.SearchRecordIdsByFilterPagedAsync(StepTenantId(), table.Id, odataFilter, null, ct))
        {
            rows.AddRange(await VerifyAiCandidatesAsync(table, fields, tree, page.Ids, null,
                inMemoryFilterFids, computedFieldFids, recordSearchService, recordRepo, pageSize, ct));
            if (limit.HasValue && rows.Count >= limit.Value) break;   // a user-configured cap, not a silent one
        }
        return rows.OrderBy(r => r.TryGetValue("Id", out var id) && id != null ? Convert.ToInt64(id, CultureInfo.InvariantCulture) : long.MaxValue).ToList();
    }

    /// <summary>
    /// Streamed search through AI Search: pages of candidate ids go to the workset one verified page at a time, with the AI cursor
    /// saved with each page, so a long search holds one page in memory and an interrupted one resumes where it stopped.
    /// Returns false — with nothing staged — when AI Search found no record or failed before the first page, so the caller reads
    /// the search from SQL. Once something is staged a failure propagates and the job's retry resumes from the cursor.
    /// </summary>
    private async Task<bool> DiscoverFromAiSearchAsync(
        AppTable table, IReadOnlyList<AppField> fields, FilterGroup? tree, string odataFilter,
        PipelineSearchWorkset workset, Guid worksetId, int? limit, long snapshotMaxRecordId, HashSet<int>? readableFids,
        HashSet<long> inMemoryFilterFids, HashSet<long> computedFieldFids,
        IPipelineRecordSearchService recordSearchService, IRecordRepository recordRepo, CancellationToken ct)
    {
        var ordinal = workset.DiscoveredCount;
        var outputFields = readableFids == null ? fields : fields.Where(f => f.Fid.HasValue && readableFids.Contains(f.Fid.Value)).ToList();
        try
        {
            await foreach (var page in StepAzureSearch()!.SearchRecordIdsByFilterPagedAsync(
                StepTenantId(), table.Id, odataFilter, workset.LastSearchCursor, ct))
            {
                var remaining = limit.HasValue ? limit.Value - ordinal : int.MaxValue;
                if (remaining <= 0) break;

                var rows = await VerifyAiCandidatesAsync(table, fields, tree, page.Ids, snapshotMaxRecordId,
                    inMemoryFilterFids, computedFieldFids, recordSearchService, recordRepo, _options.SearchRecordsPageSize, ct);
                if (rows.Count > remaining) rows = rows.Take(remaining).ToList();
                // Formula fields have no stored value: project them so loop items ({{steps.<loop>.item.fid_N}}) carry them.
                if (computedFieldFids.Count > 0 && rows.Count > 0)
                {
                    var computed = await ProjectComputedAsync(table, fields, rows, ct);
                    if (computed != null) rows = rows.Select((r, i) => MergeComputed(r, computed[i])).ToList();
                }

                var staged = new List<PipelineBulkEventRecord>(rows.Count);
                foreach (var record in rows)
                {
                    var normalized = NormalizeSearchRecord(record, outputFields);
                    if (!normalized.TryGetValue("RecordPublicId", out var publicIdValue) ||
                        !Guid.TryParse(Convert.ToString(publicIdValue, CultureInfo.InvariantCulture), out var recordPublicId))
                        throw new PipelineNonRetryableException("Search Records returned a row without a valid PublicId.");
                    staged.Add(new PipelineBulkEventRecord
                    {
                        BulkEventId = worksetId,
                        SearchWorksetId = worksetId,
                        Ordinal = ++ordinal,
                        RecordPublicId = recordPublicId,
                        EventType = "Search",
                        AfterValuesJson = JsonSerializer.Serialize(normalized),
                        Processed = 0,
                        CreatedOn = DateTime.UtcNow
                    });
                }

                // The cursor moves past every candidate on this page — including the ones SQL rejected.
                await _pipelineRepo.AppendSearchWorksetAiPageAsync(workset, staged, page.NextCursor, ct);
                workset.LastSearchCursor = page.NextCursor;
                workset.DiscoveredCount = ordinal;
                if (limit.HasValue && ordinal >= limit.Value) break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && workset.DiscoveredCount == 0)
        {
            _logger.LogWarning(ex, "[Pipeline SearchRecords] Azure AI Search failed for table {TableId} before any record was staged. Falling back to SQL.", table.Id);
            return false;
        }

        if (workset.DiscoveredCount == 0)
            _logger.LogInformation("[Pipeline SearchRecords] Azure AI Search found no matching record for table {TableId}; checking SQL.", table.Id);
        return workset.DiscoveredCount > 0;
    }
}
