using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Configurations;
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
///
/// The index lags SQL: a record created or changed moments ago may be missing from it, or still be there with its old values,
/// so AI Search would not find it. Every record changed within <see cref="PipelineExecutionOptions.AiSearchIndexLagSeconds"/>
/// before the search started is therefore also read from SQL (the "index-lag tail"), with the complete filter, and added to
/// the candidates the index found — once.
/// </summary>
public partial class PipelineEngine
{
    /// <summary>Cursor of a streamed search whose index read is done and whose index-lag tail has been read up to the record id
    /// after the prefix. Index cursors are id prefixes (hex) or <see cref="AiSearchIdPage.EndCursor"/>, never this.</summary>
    public const string AiIndexLagCursorPrefix = "lag:";

    /// <summary>Cursor of a streamed search whose index-lag tail has been read to the end: nothing is left to read.</summary>
    public const string AiIndexLagEndCursor = AiIndexLagCursorPrefix + AiSearchIdPage.EndCursor;

    /// <summary>The time from which records are re-read from SQL because the index may not have them yet, or null when that is off.</summary>
    private DateTime? AiIndexLagSince(DateTime searchStartedUtc) =>
        _options.AiSearchIndexLagSeconds > 0 ? searchStartedUtc.AddSeconds(-_options.AiSearchIndexLagSeconds) : null;

    /// <summary>When a streamed search began: its workset's creation time (set by the database), so a resumed search keeps it.</summary>
    private static DateTime SearchStartedUtc(PipelineSearchWorkset workset) =>
        workset.CreatedOn == default ? DateTime.UtcNow : workset.CreatedOn;

    /// <summary>Whether a streamed AI search has read everything it needs from the index (only the index-lag tail may be left).</summary>
    public static bool IsAiIndexReadDone(string? cursor) =>
        cursor == AiSearchIdPage.EndCursor || (cursor?.StartsWith(AiIndexLagCursorPrefix, StringComparison.Ordinal) ?? false);
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

    /// <summary>Whether the table's app encrypts its fields (meta.App.IsEncrypted). Its columns then hold ciphertext even where
    /// the field's own IsEncrypted flag is off, so no SQL condition on them can match.</summary>
    private async Task<bool> IsAppEncryptedAsync(AppTable table, CancellationToken ct) =>
        CurrentStepServices.GetService(typeof(IAppRepository)) is IAppRepository apps
        && (await apps.GetByIdAsync(table.AppId, ct)).IsEncrypted;

    /// <summary>Whether this search may be answered by AI Search (with the filter to use), or must be read from SQL. The checks of
    /// <see cref="PipelineAiSearchGate"/> are made when a search starts (<paramref name="applyGates"/>), never while one is resumed: a
    /// search keeps the source it began with.</summary>
    private async Task<PipelineAiSearchPlan> PlanAiSearchAsync(
        AppTable table, IReadOnlyList<AppField> fields, FilterGroup? filterTree, IRecordRepository recordRepo, CancellationToken ct, bool applyGates = true)
    {
        var azure = StepAzureSearch();
        if (azure == null || !azure.IsGridSearchEnabled)
            return new PipelineAiSearchPlan(null, "Azure AI Search is not enabled");

        var plan = PipelineAiSearchPlanner.Evaluate(filterTree, fields, new PipelineAiSearchPlanOptions(_options.AiSearchPatternOperators));
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
        if (applyGates)
        {
            var gate = await PipelineAiSearchGate.EvaluateAsync(table, fields, plan, azure, StepTenantId(), recordRepo, _options, ct);
            if (!gate.UseAiSearch)
            {
                _logger.LogInformation("[Pipeline SearchRecords] Table {TableId} is searched in SQL, not Azure AI Search: {Reason}.", table.Id, gate.SqlReason);
                return new PipelineAiSearchPlan(null, gate.SqlReason);
            }
        }
        return plan;
    }

    /// <summary>With a MaxResults the search returns the first N records by record id, as SQL does, while the index lists ids in its own
    /// order. The index is therefore used only when it finds few enough candidates to read them all, sort them by record id and cut to N.
    /// More candidates (or none) are read from SQL, which stops at N.</summary>
    private async Task<bool> AiMatchesFitLimitedReadAsync(string odataFilter, AppTable table, string? targetIndex, CancellationToken ct)
    {
        var (_, total) = await StepAzureSearch()!.SearchRecordsByFilterPagedAsync(StepTenantId(), table.Id, odataFilter, 1, 1, targetIndex, ct);
        return total > 0 && total <= _options.AiSearchLimitedReadMaxMatches;
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
            rows.AddRange(await VerifyRecordIdsAsync(table, fields, tree, recordIds, maxRecordId,
                inMemoryFilterFids, computedFieldFids, recordSearchService, recordRepo, pageSize, ct));
        }
        return rows.OrderBy(RowRecordId).ToList();
    }

    /// <summary>Reads these records from SQL with the complete filter applied: only the ones SQL itself matches come back.</summary>
    private async Task<List<IReadOnlyDictionary<string, object?>>> VerifyRecordIdsAsync(
        AppTable table, IReadOnlyList<AppField> fields, FilterGroup? tree, IReadOnlyList<long> recordIds, long? maxRecordId,
        HashSet<long> inMemoryFilterFids, HashSet<long> computedFieldFids,
        IPipelineRecordSearchService recordSearchService, IRecordRepository recordRepo, int pageSize, CancellationToken ct)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var idChunk in recordIds.Chunk(AiCandidateRecordIdChunkSize))
        {
            var (found, _) = await SearchSqlAsync(table, fields, WithIdRestriction(tree, idChunk, maxRecordId), null,
                inMemoryFilterFids, computedFieldFids, recordSearchService, recordRepo, pageSize, ct);
            rows.AddRange(found);
        }
        return rows;
    }

    private static long RowRecordId(IReadOnlyDictionary<string, object?> row) =>
        row.TryGetValue("Id", out var id) && id != null ? Convert.ToInt64(id, CultureInfo.InvariantCulture) : long.MaxValue;

    private static Guid RowPublicId(IReadOnlyDictionary<string, object?> row) =>
        row.TryGetValue("PublicId", out var publicId) && Guid.TryParse(Convert.ToString(publicId, CultureInfo.InvariantCulture), out var parsed)
            ? parsed : Guid.Empty;

    /// <summary>One page of the index-lag tail: the records changed since <paramref name="since"/> with an id above
    /// <paramref name="afterId"/>, read from SQL with the complete filter. <c>Changed</c> is empty once the tail is read to the end.</summary>
    private async Task<(List<IReadOnlyDictionary<string, object?>> Rows, IReadOnlyList<(long Id, DateTime ChangedOn)> Changed)> ReadAiIndexLagPageAsync(
        AppTable table, IReadOnlyList<AppField> fields, FilterGroup? tree, DateTime since, long afterId, long maxRecordId,
        HashSet<long> inMemoryFilterFids, HashSet<long> computedFieldFids,
        IPipelineRecordSearchService recordSearchService, IRecordRepository recordRepo, int pageSize, CancellationToken ct)
    {
        var changed = await recordRepo.ListRecordsChangedSinceAsync(table, since, afterId, maxRecordId, AiCandidateRecordIdChunkSize, ct);
        if (changed.Count == 0) return (new List<IReadOnlyDictionary<string, object?>>(), changed);
        var rows = await VerifyRecordIdsAsync(table, fields, tree, changed.Select(c => c.Id).ToList(), null,
            inMemoryFilterFids, computedFieldFids, recordSearchService, recordRepo, pageSize, ct);
        return (rows.OrderBy(RowRecordId).ToList(), changed);
    }

    /// <summary>Non-streamed search through AI Search: every candidate id, verified in SQL, plus the index-lag tail. Stops once
    /// <paramref name="limit"/> (a user-configured MaxResults) rows are in hand. Throws if AI Search fails; the caller falls back to SQL.
    /// Returns no rows when the index found none that SQL matches, so the caller reads the search from SQL.</summary>
    private async Task<List<IReadOnlyDictionary<string, object?>>> ReadAiSearchRowsAsync(
        AppTable table, IReadOnlyList<AppField> fields, FilterGroup? tree, string odataFilter, int? limit,
        HashSet<long> inMemoryFilterFids, HashSet<long> computedFieldFids,
        IPipelineRecordSearchService recordSearchService, IRecordRepository recordRepo, int pageSize, CancellationToken ct)
    {
        var lagSince = AiIndexLagSince(DateTime.UtcNow);
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var targetIndex = PowerBase.Application.Search.SearchSlotResolver.IsWideTable(fields)
            ? PowerBase.Application.Search.SearchSlotResolver.GetTableIndexName(table.Id)
            : null;
        // A MaxResults is answered from the index only when every candidate can be read (see AiMatchesFitLimitedReadAsync); otherwise
        // no rows come back and the caller reads the search from SQL.
        if (limit.HasValue && !await AiMatchesFitLimitedReadAsync(odataFilter, table, targetIndex, ct)) return rows;
        var searchPages = targetIndex != null
            ? StepAzureSearch()!.SearchRecordIdsByFilterPagedAsync(StepTenantId(), table.Id, odataFilter, null, targetIndex, ct)
            : StepAzureSearch()!.SearchRecordIdsByFilterPagedAsync(StepTenantId(), table.Id, odataFilter, null, ct);
        await foreach (var page in searchPages)
        {
            rows.AddRange(await VerifyAiCandidatesAsync(table, fields, tree, page.Ids, null,
                inMemoryFilterFids, computedFieldFids, recordSearchService, recordRepo, pageSize, ct));
        }

        // The records changed too recently for the index to be trusted with them, each added once (the index may have found it too).
        // Read with a MaxResults too: a recently changed record can be among the first N by record id.
        if (lagSince is { } since && rows.Count > 0)
        {
            var found = rows.Select(RowRecordId).ToHashSet();
            for (var afterId = 0L; ;)
            {
                var (lagRows, changed) = await ReadAiIndexLagPageAsync(table, fields, tree, since, afterId, long.MaxValue,
                    inMemoryFilterFids, computedFieldFids, recordSearchService, recordRepo, pageSize, ct);
                rows.AddRange(lagRows.Where(r => found.Add(RowRecordId(r))));
                if (changed.Count < AiCandidateRecordIdChunkSize) break;
                afterId = changed[^1].Id;
            }
        }
        return rows.OrderBy(RowRecordId).ToList();
    }

    /// <summary>
    /// Streamed search through AI Search: pages of candidate ids go to the workset one verified page at a time, with the AI cursor
    /// saved with each page, so a long search holds one page in memory and an interrupted one resumes where it stopped.
    /// Then the index-lag tail is staged the same way, its cursor (<see cref="AiIndexLagCursorPrefix"/>) saved with each page.
    /// <paramref name="odataFilter"/> may be null only when the index read is already done (<see cref="IsAiIndexReadDone"/>).
    /// Returns false — with nothing staged — when AI Search found no record or failed before the first page, so the caller reads
    /// the search from SQL. Once something is staged a failure propagates and the job's retry resumes from the cursor.
    /// </summary>
    private async Task<bool> DiscoverFromAiSearchAsync(
        AppTable table, IReadOnlyList<AppField> fields, FilterGroup? tree, string? odataFilter,
        PipelineSearchWorkset workset, Guid worksetId, int? limit, long snapshotMaxRecordId, HashSet<int>? readableFids,
        HashSet<long> inMemoryFilterFids, HashSet<long> computedFieldFids,
        IPipelineRecordSearchService recordSearchService, IRecordRepository recordRepo, CancellationToken ct)
    {
        var ordinal = workset.DiscoveredCount;
        var outputFields = readableFids == null ? fields : fields.Where(f => f.Fid.HasValue && readableFids.Contains(f.Fid.Value)).ToList();
        var targetIndex = PowerBase.Application.Search.SearchSlotResolver.IsWideTable(fields)
            ? PowerBase.Application.Search.SearchSlotResolver.GetTableIndexName(table.Id)
            : null;
        // Fixed by the workset, so a resumed search re-reads exactly the same records as the interrupted one would have.
        var searchStarted = SearchStartedUtc(workset);
        var lagSince = AiIndexLagSince(searchStarted);

        async Task StageAsync(List<IReadOnlyDictionary<string, object?>> rows, string nextCursor)
        {
            // Formula fields have no stored value: project them so loop items ({{steps.<loop>.item.fid_N}}) carry them.
            if (computedFieldFids.Count > 0 && rows.Count > 0)
            {
                var computed = await ProjectComputedAsync(table, fields, rows, ct);
                if (computed != null) rows = rows.Select((r, i) => MergeComputed(r, computed[i])).ToList();
            }

            var staged = new List<PipelineBulkEventRecord>(rows.Count);
            var stagedOrdinal = ordinal;
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
                    Ordinal = ++stagedOrdinal,
                    RecordPublicId = recordPublicId,
                    EventType = "Search",
                    AfterValuesJson = JsonSerializer.Serialize(normalized),
                    Processed = 0,
                    CreatedOn = DateTime.UtcNow
                });
            }

            await _pipelineRepo.AppendSearchWorksetAiPageAsync(workset, staged, nextCursor, ct);
            ordinal = stagedOrdinal;
            workset.LastSearchCursor = nextCursor;
            workset.DiscoveredCount = ordinal;
        }

        var limitReached = limit.HasValue && ordinal >= limit.Value;
        try
        {
            // A MaxResults: the first N records by record id, as SQL returns them. The index lists ids in its own order, so when it finds
            // few enough candidates they are all read (the index-lag tail too), sorted by record id, cut to N and staged in one go; when
            // it finds more (or none) the search is read from SQL, which stops at N.
            if (limit.HasValue && odataFilter != null && ordinal == 0 && !IsAiIndexReadDone(workset.LastSearchCursor))
            {
                if (!await AiMatchesFitLimitedReadAsync(odataFilter, table, targetIndex, ct)) return false;
                var all = new List<IReadOnlyDictionary<string, object?>>();
                var limitedPages = targetIndex != null
                    ? StepAzureSearch()!.SearchRecordIdsByFilterPagedAsync(StepTenantId(), table.Id, odataFilter, null, targetIndex, ct)
                    : StepAzureSearch()!.SearchRecordIdsByFilterPagedAsync(StepTenantId(), table.Id, odataFilter, null, ct);
                await foreach (var limitedPage in limitedPages)
                    all.AddRange(await VerifyAiCandidatesAsync(table, fields, tree, limitedPage.Ids, snapshotMaxRecordId,
                        inMemoryFilterFids, computedFieldFids, recordSearchService, recordRepo, _options.SearchRecordsPageSize, ct));
                if (lagSince is { } limitedSince)
                {
                    var seen = all.Select(RowRecordId).ToHashSet();
                    for (var afterId = 0L; ;)
                    {
                        var (lagRows, changed) = await ReadAiIndexLagPageAsync(table, fields, tree, limitedSince, afterId, workset.SnapshotMaxRecordId,
                            inMemoryFilterFids, computedFieldFids, recordSearchService, recordRepo, _options.SearchRecordsPageSize, ct);
                        all.AddRange(lagRows.Where(r => seen.Add(RowRecordId(r))));
                        if (changed.Count < AiCandidateRecordIdChunkSize) break;
                        afterId = changed[^1].Id;
                    }
                }
                var firstN = all.OrderBy(RowRecordId).Take(limit.Value).ToList();
                if (firstN.Count == 0) return false;
                await StageAsync(firstN, AiIndexLagEndCursor);
                limitReached = true;
            }

            if (!IsAiIndexReadDone(workset.LastSearchCursor) && !limitReached)
            {
                var searchPages = targetIndex != null
                    ? StepAzureSearch()!.SearchRecordIdsByFilterPagedAsync(StepTenantId(), table.Id, odataFilter!, workset.LastSearchCursor, targetIndex, ct)
                    : StepAzureSearch()!.SearchRecordIdsByFilterPagedAsync(StepTenantId(), table.Id, odataFilter!, workset.LastSearchCursor, ct);
                await foreach (var page in searchPages)
                {
                    var rows = await VerifyAiCandidatesAsync(table, fields, tree, page.Ids, snapshotMaxRecordId,
                        inMemoryFilterFids, computedFieldFids, recordSearchService, recordRepo, _options.SearchRecordsPageSize, ct);
                    // A record changed too recently for the index to be trusted with it is left to the index-lag tail, which
                    // reads every such record from SQL: staged from there, never from here too.
                    if (lagSince is { } since && rows.Count > 0)
                    {
                        var recent = await recordRepo.GetRecordIdsChangedSinceAsync(table, rows.Select(RowRecordId).ToList(), since, ct);
                        if (recent.Count > 0) rows = rows.Where(r => !recent.Contains(RowRecordId(r))).ToList();
                    }
                    var remaining = limit.HasValue ? limit.Value - ordinal : int.MaxValue;
                    if (rows.Count > remaining) rows = rows.Take(remaining).ToList();

                    // The cursor moves past every candidate on this page — including the ones SQL rejected.
                    await StageAsync(rows, page.NextCursor);
                    if (limit.HasValue && ordinal >= limit.Value) { limitReached = true; break; }
                }
            }

            // Only once the index found something: when it found nothing the caller reads the whole search from SQL anyway.
            if (lagSince is { } lagFrom && ordinal > 0 && !limitReached)
            {
                var afterId = workset.LastSearchCursor?.StartsWith(AiIndexLagCursorPrefix, StringComparison.Ordinal) == true
                    && workset.LastSearchCursor != AiIndexLagEndCursor
                    ? long.Parse(workset.LastSearchCursor[AiIndexLagCursorPrefix.Length..], CultureInfo.InvariantCulture)
                    : 0L;
                while (workset.LastSearchCursor != AiIndexLagEndCursor)
                {
                    var (rows, changed) = await ReadAiIndexLagPageAsync(table, fields, tree, lagFrom, afterId, workset.SnapshotMaxRecordId,
                        inMemoryFilterFids, computedFieldFids, recordSearchService, recordRepo, _options.SearchRecordsPageSize, ct);
                    // A record staged from the index was not recent when its page was read; it is recent now only if it changed
                    // after the search began. Only those can be in the workset already.
                    var changedDuringSearch = changed.Where(c => c.ChangedOn >= searchStarted).Select(c => c.Id).ToHashSet();
                    if (changedDuringSearch.Count > 0)
                    {
                        var candidates = rows.Where(r => changedDuringSearch.Contains(RowRecordId(r))).Select(RowPublicId).ToList();
                        var already = await _pipelineRepo.GetStagedSearchWorksetRecordsAsync(worksetId, candidates, ct);
                        if (already.Count > 0) rows = rows.Where(r => !already.Contains(RowPublicId(r))).ToList();
                    }
                    var remaining = limit.HasValue ? limit.Value - ordinal : int.MaxValue;
                    if (rows.Count > remaining) rows = rows.Take(remaining).ToList();

                    var done = changed.Count < AiCandidateRecordIdChunkSize;
                    if (!done) afterId = changed[^1].Id;
                    await StageAsync(rows, done ? AiIndexLagEndCursor : AiIndexLagCursorPrefix + afterId.ToString(CultureInfo.InvariantCulture));
                    if (limit.HasValue && ordinal >= limit.Value) break;
                }
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
