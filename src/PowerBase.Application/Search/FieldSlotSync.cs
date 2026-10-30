using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;

namespace PowerBase.Application.Search;

/// <summary>
/// Pushes one field's change into the index after the field is switched on or off for search, restored, or converted to another
/// type. Each record is rewritten in full (every indexed value, and the free-text field rebuilt from the searchable text alone), so
/// switching Searchable on or off also adds or removes the field's text from quick search. The index column(s) come from the fields'
/// stored slots, so they are the same ones the indexer worker writes and the report queries read.
/// </summary>
public static class FieldSlotSync
{
    /// <param name="ClearSlot">Column(s) the field used before and must be emptied: a type change that moved it to another column, or a
    /// deleted field. Null for a switch on or off (a field switched off is cleared where it is stored).</param>
    public sealed record Plan(SearchTableSlotInfo Info, int Fid, bool IsNullify, SlotRef? ClearSlot = null)
    {
        /// <summary>False when the field has no column to write to or empty (no slot free, or not allocated): nothing to upload.</summary>
        public bool HasTarget =>
            Info.SlotMap.ContainsKey(Fid)
            || (IsNullify && Info.StoredSlots.ContainsKey(Fid))
            || (Info.MigratingToWide && Info.WideSlotMap!.ContainsKey(Fid))
            || ClearSlot != null;

        /// <summary>The columns to empty in each record: the old column of a moved or deleted field, the column of a field that no longer
        /// has an indexed value (it became a formula), or the stored column of a field that is switched off. A backfill never empties the
        /// column the field writes now. Emptying a column another field now uses is harmless: that field's value is written over it.</summary>
        public SearchIndexDocumentBuilder.FieldColumnClear? Clear
        {
            get
            {
                Info.SlotMap.TryGetValue(Fid, out var current);
                if (ClearSlot != null)
                {
                    var stillUsed = !IsNullify && current != null && string.Equals(current.Slot, ClearSlot.Slot, StringComparison.OrdinalIgnoreCase);
                    return new(Fid, stillUsed ? null : ClearSlot);
                }
                if (!IsNullify) return null;
                return new(Fid, current ?? (Info.StoredSlots.TryGetValue(Fid, out var stored) ? stored : null));
            }
        }
    }

    public static async Task<Plan> PlanAsync(IAppFieldRepository fieldRepo, long tableId, int fid, bool isNullify, CancellationToken ct, SlotRef? clearSlot = null)
    {
        // A field that has just become eligible has no slot yet: give it one before anything is written.
        await fieldRepo.EnsureSearchSlotsAsync(tableId, ct);
        var all = await fieldRepo.ListByTableAsync(tableId, ct);
        return new Plan(SearchTableSlotInfo.From(tableId, all), fid, isNullify, clearSlot);
    }

    /// <summary>The documents to upload: each record rewritten in full from <paramref name="docs"/> (full database reads of the records),
    /// with the field's old column emptied, for the table's current index and, while the table is moving to its own index, that one too.</summary>
    public static IReadOnlyList<SearchIndexDocument> ToDocuments(IEnumerable<SearchIndexDocument> docs, Plan plan)
    {
        if (!plan.HasTarget) return [];
        var clear = plan.Clear;
        return docs
            .SelectMany(d => SearchIndexDocumentBuilder.BuildRebuild(plan.Info, d, clear))
            .Where(d => d.SlotValues is { Count: > 0 })
            .ToList();
    }

    /// <summary>
    /// Brings the index up to date after one field changed, in the order that keeps it right if any step fails: the first page of
    /// records is rewritten now (so the change shows at once), then the rest is queued for the indexer worker, which continues after
    /// the last record written here. A failure on the first page is logged and the queued job starts from the beginning instead, so a
    /// field change that is already saved never leaves the index half done. Never throws (the field is saved by then), and the job is
    /// queued even if the request is cancelled after the first page.
    /// </summary>
    public static async Task SyncAsync(
        IAppFieldRepository fieldRepo, IRecordRepository recordRepo, IAzureSearchService search, IMessagePublisher publisher,
        long tenantId, long appId, long tableId, int fid, bool isNullify, SlotRef? clearSlot, Action<Exception, string>? logError, CancellationToken ct,
        ISearchFieldJobs? jobs = null)
    {
        const long nothingLeft = -1;
        long afterId = 0;
        long? jobId = null;
        try
        {
            var plan = await PlanAsync(fieldRepo, tableId, fid, isNullify, ct, clearSlot);
            if (!plan.HasTarget)
            {
                afterId = nothingLeft; // no column to write or empty: the index does not hold this field
            }
            else
            {
                // Recorded before anything is written: from here on the job is resumed if any later step is lost.
                jobId = await RecordJobAsync(jobs, Message(tenantId, appId, tableId, fid, isNullify, clearSlot, 0), logError);
                var (docs, lastId) = await recordRepo.GetTableIndexPageAsync(tenantId, tableId, 0, SearchWideMigration.PageSize, ct);
                if (docs.Count > 0)
                    await search.BulkIndexRecordsAsync(ToDocuments(docs, plan), ct);
                // Fewer than a page: every record is done, nothing to queue.
                afterId = docs.Count < SearchWideMigration.PageSize ? nothingLeft : lastId;
            }
        }
        catch (Exception ex)
        {
            afterId = 0; // the worker redoes the first page
            if (ex is not OperationCanceledException)
                logError?.Invoke(ex, $"Search index update for field {fid} of table {tableId} could not write its first page; the indexer will do it.");
        }

        try
        {
            // Switching this field on can push the table past what the shared index takes: start moving it to its own index.
            await SearchWideMigration.StartIfNeededAsync(fieldRepo, publisher, search, tenantId, appId, tableId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logError?.Invoke(ex, $"Could not check whether table {tableId} has to move to its own search index; the indexer checks again.");
        }

        try
        {
            if (afterId == nothingLeft)
            {
                if (jobId is long done) await jobs!.CompleteAsync(done, 0, CancellationToken.None);
                return;
            }
            // The job row must say where the queued job starts, or the worker's compare-and-set would stop it: redo the first page instead.
            if (jobId is long running && afterId > 0 && !await jobs!.AdvanceAsync(running, 0, afterId, CancellationToken.None))
                afterId = 0;
        }
        catch (Exception ex)
        {
            if (afterId == nothingLeft) return; // the row stays; the relay re-runs the job, which finds nothing left to do and removes it
            afterId = 0;
            logError?.Invoke(ex, $"Could not record the progress of the search index job for field {fid} of table {tableId}; the indexer redoes the first page.");
        }

        await PublishAsync(publisher, Message(tenantId, appId, tableId, fid, isNullify, clearSlot, afterId, jobId), logError, CancellationToken.None);
    }

    public static SearchIndexMessage Message(long tenantId, long appId, long tableId, long fid, bool isNullify, SlotRef? clearSlot, long afterId, long? jobId = null) => new()
    {
        Action = isNullify ? IndexAction.NullifyField : IndexAction.BackfillField,
        TenantId = tenantId,
        AppId = appId,
        TableId = tableId,
        FieldId = fid,
        Page = 1,
        AfterId = afterId,
        JobId = jobId,
        Slot = clearSlot?.Slot,
        EndSlot = clearSlot?.EndSlot
    };

    /// <summary>Records a field job before its first message is sent (see <see cref="ISearchFieldJobs"/>). Null when there is nowhere to
    /// record it, or recording failed: the job then runs unrecorded, as jobs did before they were recorded.</summary>
    public static async Task<long?> RecordJobAsync(ISearchFieldJobs? jobs, SearchIndexMessage job, Action<Exception, string>? logError)
    {
        if (jobs == null) return null;
        try
        {
            return await jobs.StartAsync(job, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logError?.Invoke(ex, $"Could not record the search index job {job.Action} for field {job.FieldId} of table {job.TableId}; it runs unrecorded.");
            return null;
        }
    }

    /// <summary>Sends a field job and waits for the answer, retrying a failed send a few times before giving up with an error in the log.
    /// A recorded job (<see cref="SearchIndexMessage.JobId"/>) is then re-sent by the search relay from where it stopped.</summary>
    public static async Task PublishAsync(IMessagePublisher publisher, SearchIndexMessage message, Action<Exception, string>? logError, CancellationToken ct)
    {
        const int attempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await publisher.PublishAsync(message, ct);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt >= attempts)
                {
                    logError?.Invoke(ex, message.JobId.HasValue
                        ? $"Search index job {message.Action} for field {message.FieldId} of table {message.TableId} could not be queued after {attempts} attempts; the search relay re-sends it."
                        : $"Search index job {message.Action} for field {message.FieldId} of table {message.TableId} could not be queued after {attempts} attempts. Run the search reindex for this table to repair it.");
                    return;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct);
            }
        }
    }
}

/// <summary>
/// Moves a table that has outgrown the shared index into its own index. The order is what keeps search correct meanwhile:
/// 1. while the table is "migrating" every write goes to both indexes and queries keep using the shared one, which stays complete;
/// 2. a ReindexTable message copies every existing record to the new index, page by page;
/// 3. after the last page the table is marked wide (queries switch to the new index in one step) and the old documents are removed.
/// </summary>
public static class SearchWideMigration
{
    public const int PageSize = 500;

    /// <summary>Starts the copy if the table has more eligible fields than the shared index takes and has not been moved. Returns whether it started one.</summary>
    public static async Task<bool> StartIfNeededAsync(
        IAppFieldRepository fieldRepo, IMessagePublisher publisher, IAzureSearchService search, long tenantId, long appId, long tableId, CancellationToken ct)
    {
        var fields = await fieldRepo.ListByTableAsync(tableId, ct);
        if (!SearchSlotResolver.ShouldBeWide(fields)) return false;
        // No room for one more index on the search service: the table stays in the shared index (fields past its slots are answered from SQL).
        if (!await search.CanUseWideIndexAsync(tableId, ct)) return false;
        await publisher.PublishAsync(Message(tenantId, appId, tableId, 1, afterId: 0), ct);
        return true;
    }

    /// <param name="afterId">The id the copy continues after (0 from the start); null only for a message that pages by number.</param>
    public static SearchIndexMessage Message(long tenantId, long appId, long tableId, int page, long? afterId = null) => new()
    {
        Action = IndexAction.ReindexTable,
        TenantId = tenantId,
        AppId = appId,
        TableId = tableId,
        Page = page,
        AfterId = afterId
    };

    /// <summary>Ends the migration after the last page: queries switch to the table's own index, and the documents left in the shared index are removed.
    /// Safe to repeat. Does nothing if the table is not (or no longer) waiting to be moved.</summary>
    public static async Task<bool> FinalizeAsync(IAppFieldRepository fieldRepo, IAzureSearchService search, long tenantId, long tableId, CancellationToken ct)
    {
        var fields = await fieldRepo.ListByTableAsync(tableId, ct);
        if (!SearchSlotResolver.ShouldBeWide(fields)) return false;
        if (!await search.CanUseWideIndexAsync(tableId, ct)) return false; // never switch queries to an index that cannot exist

        await fieldRepo.MarkTableWideAsync(tableId, ct);
        await search.PurgeTableFromSharedIndexesAsync(tenantId, tableId, ct);
        return true;
    }
}
