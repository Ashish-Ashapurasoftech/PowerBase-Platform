using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;

namespace PowerBase.Application.Search;

/// <summary>
/// Pushes one field's values into the index after the field is switched on or off for search (or restored). The index
/// column(s) come from the field's stored slot, so they are the same ones the indexer worker writes and the report queries
/// read, and no other field's column can move because of this change.
/// </summary>
public static class FieldSlotSync
{
    public sealed record Plan(SearchTableSlotInfo Info, int Fid, bool IsNullify)
    {
        /// <summary>False when the field has no column to write to (no slot free, or not allocated): nothing to upload.</summary>
        public bool HasTarget =>
            Info.SlotMap.ContainsKey(Fid)
            || (IsNullify && Info.StoredSlots.ContainsKey(Fid))
            || (Info.MigratingToWide && Info.WideSlotMap!.ContainsKey(Fid));
    }

    public static async Task<Plan> PlanAsync(IAppFieldRepository fieldRepo, long tableId, int fid, bool isNullify, CancellationToken ct)
    {
        // A field that has just become eligible has no slot yet: give it one before anything is written.
        await fieldRepo.EnsureSearchSlotsAsync(tableId, ct);
        var all = await fieldRepo.ListByTableAsync(tableId, ct);
        return new Plan(SearchTableSlotInfo.From(tableId, all), fid, isNullify);
    }

    /// <summary>The documents to upload: each record's value for the field in its column(s) (null when switching off), for the table's
    /// current index and, while the table is moving to its own index, that one too.</summary>
    public static IReadOnlyList<SearchIndexDocument> ToDocuments(IEnumerable<SearchIndexDocument> docs, Plan plan)
    {
        if (!plan.HasTarget) return [];
        return docs
            .SelectMany(d => SearchIndexDocumentBuilder.BuildFieldChange(plan.Info, d.TenantId, d.AppId, d.TableId, d.PublicId, d.Values, plan.Fid, plan.IsNullify))
            .Where(d => d.SlotValues is { Count: > 0 })
            .ToList();
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
        await publisher.PublishAsync(Message(tenantId, appId, tableId, 1), ct);
        return true;
    }

    public static SearchIndexMessage Message(long tenantId, long appId, long tableId, int page) => new()
    {
        Action = IndexAction.ReindexTable,
        TenantId = tenantId,
        AppId = appId,
        TableId = tableId,
        Page = page
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
