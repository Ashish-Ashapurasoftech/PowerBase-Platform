using System;
using System.Collections.Generic;
using System.Linq;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Search;

/// <summary>
/// Where each field of one table is stored in the index right now, derived only from the fields' stored slots.
/// Used by the indexer, the backfill and the field-change sync, so they all write the same columns the queries read.
/// </summary>
public sealed record SearchTableSlotInfo(
    IReadOnlyDictionary<long, SlotRef> SlotMap,
    IReadOnlyDictionary<long, SlotRef> StoredSlots,
    IReadOnlySet<long> AllFids,
    bool IsWide,
    string? TargetIndex,
    IReadOnlySet<string> SearchTextSlots,
    IReadOnlyDictionary<long, SlotRef>? WideSlotMap,
    IReadOnlySet<string>? WideSearchTextSlots)
{
    /// <summary>The table has outgrown the shared index and is being moved: every write goes to both indexes until the move ends.</summary>
    public bool MigratingToWide => WideSlotMap != null;

    public string? WideIndexName(long tableId) => MigratingToWide ? SearchSlotResolver.GetTableIndexName(tableId) : null;

    /// <param name="fields">The table's non-deleted fields as returned by <see cref="IAppFieldRepository.ListByTableAsync"/> (with their stored slots).</param>
    public static SearchTableSlotInfo From(long tableId, IReadOnlyList<AppField> fields)
    {
        var isWide = SearchSlotResolver.IsWideTable(fields);
        var migrating = SearchSlotResolver.ShouldBeWide(fields);

        var mapped = new Dictionary<long, SlotRef>();
        var stored = new Dictionary<long, SlotRef>();
        var searchText = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allFids = new HashSet<long>();
        var wideMap = migrating ? new Dictionary<long, SlotRef>() : null;
        var wideSearchText = migrating ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : null;

        foreach (var f in fields)
        {
            if (!f.Fid.HasValue) continue;
            var fid = (long)f.Fid.Value;
            allFids.Add(fid);

            // The stored slot of ANY field (even one switched off), so a switch-off can clear it.
            if (isWide)
                stored[fid] = SearchSlotResolver.WideRef(f);
            else if (SearchSlotResolver.SlotMatchesType(f.SearchSlot, f.TypeCode))
                stored[fid] = new SlotRef(f.SearchSlot!, f.TypeCode, f.SearchSlotEnd);

            if (!SearchSlotResolver.IsSearchEligible(f)) continue;

            if (migrating)
            {
                var wide = SearchSlotResolver.WideRef(f);
                wideMap![fid] = wide;
                if (f.IsSearchable) wideSearchText!.Add(wide.Slot);
            }

            if (isWide)
            {
                var wide = SearchSlotResolver.WideRef(f);
                mapped[fid] = wide;
                if (f.IsSearchable) searchText.Add(wide.Slot);
            }
            else if (SearchSlotResolver.SlotMatchesType(f.SearchSlot, f.TypeCode))
            {
                // No valid slot (not allocated yet, or its pool is full) means "answered from SQL, not indexed here".
                mapped[fid] = new SlotRef(f.SearchSlot!, f.TypeCode, f.SearchSlotEnd);
                if (f.IsSearchable) searchText.Add(f.SearchSlot!);
            }
        }

        return new SearchTableSlotInfo(
            mapped, stored, allFids, isWide,
            isWide ? SearchSlotResolver.GetTableIndexName(tableId) : null,
            searchText, wideMap, wideSearchText);
    }
}

/// <summary>Turns one record's current values into the index document(s) for its table.</summary>
public static class SearchIndexDocumentBuilder
{
    /// <summary>
    /// One document for the table's current index, plus a second one for its own index while the table is migrating to wide.
    /// <paramref name="values"/> maps field number to the plain (decrypted) value; a field absent from it is left untouched in
    /// the index, a null clears it.
    /// </summary>
    public static IReadOnlyList<SearchIndexDocument> Build(
        SearchTableSlotInfo info, long tenantId, long appId, long tableId, Guid publicId,
        IReadOnlyDictionary<long, object?> values, long? sourceVersion)
    {
        var docs = new List<SearchIndexDocument>(2)
        {
            new(tenantId, appId, tableId, publicId, values, Slots(info.SlotMap, values), info.TargetIndex, info.SearchTextSlots, sourceVersion)
        };

        if (info.MigratingToWide)
            docs.Add(new SearchIndexDocument(tenantId, appId, tableId, publicId, values, Slots(info.WideSlotMap!, values),
                info.WideIndexName(tableId), info.WideSearchTextSlots, sourceVersion));

        return docs;
    }

    /// <summary>A single field's change (backfill, or clearing it when it is switched off), for the current index and the wide one while migrating.</summary>
    public static IReadOnlyList<SearchIndexDocument> BuildFieldChange(
        SearchTableSlotInfo info, long tenantId, long appId, long tableId, Guid publicId,
        IReadOnlyDictionary<long, object?> values, long fid, bool isNullify)
    {
        var docs = new List<SearchIndexDocument>(2);

        var primary = new Dictionary<string, object?>();
        SlotRef? slot = null;
        if (info.SlotMap.TryGetValue(fid, out var s)) slot = s;
        else if (isNullify && info.StoredSlots.TryGetValue(fid, out var st)) slot = st; // switched off: clear where it was stored
        if (slot != null) Fill(primary, slot, values, fid, isNullify);
        docs.Add(new SearchIndexDocument(tenantId, appId, tableId, publicId, values, primary, info.TargetIndex, info.SearchTextSlots));

        if (info.MigratingToWide && info.WideSlotMap!.TryGetValue(fid, out var wideSlot))
        {
            var wide = new Dictionary<string, object?>();
            Fill(wide, wideSlot, values, fid, isNullify);
            docs.Add(new SearchIndexDocument(tenantId, appId, tableId, publicId, values, wide, info.WideIndexName(tableId), info.WideSearchTextSlots));
        }
        return docs;
    }

    /// <summary>Clears the named columns (a deleted field's) for one record, in the table's index and, while it is moving, in its own index.</summary>
    public static IReadOnlyList<SearchIndexDocument> BuildExplicitClear(
        SearchTableSlotInfo info, SearchIndexDocument record, long fid, string slot, string? endSlot)
    {
        var primary = new Dictionary<string, object?> { [slot] = null };
        if (endSlot != null) primary[endSlot] = null;
        var docs = new List<SearchIndexDocument>(2)
        {
            new(record.TenantId, record.AppId, record.TableId, record.PublicId, record.Values, primary, info.TargetIndex, info.SearchTextSlots)
        };
        if (info.MigratingToWide)
        {
            var wide = new Dictionary<string, object?> { [SearchSlotResolver.WideSlot((int)fid)] = null, [SearchSlotResolver.WideEndSlot((int)fid)] = null };
            docs.Add(new SearchIndexDocument(record.TenantId, record.AppId, record.TableId, record.PublicId, record.Values, wide, info.WideIndexName(record.TableId), info.WideSearchTextSlots));
        }
        return docs;
    }

    private static void Fill(Dictionary<string, object?> target, SlotRef slot, IReadOnlyDictionary<long, object?> values, long fid, bool isNullify)
    {
        if (isNullify) SearchSlotResolver.AddClearedSlots(target, slot);
        else if (values.TryGetValue(fid, out var v)) SearchSlotResolver.AddSlotValues(target, slot, v);
    }

    private static Dictionary<string, object?> Slots(IReadOnlyDictionary<long, SlotRef> map, IReadOnlyDictionary<long, object?> values)
    {
        var slotValues = new Dictionary<string, object?>();
        foreach (var kvp in values)
            if (map.TryGetValue(kvp.Key, out var slot))
                SearchSlotResolver.AddSlotValues(slotValues, slot, kvp.Value);
        return slotValues;
    }

    /// <summary>
    /// Merges several documents for the same record (same index) into one, oldest first, so a later value wins and a document
    /// is never sent twice in one request (the service does not define which duplicate wins). The result carries the highest
    /// source version.
    /// </summary>
    public static IReadOnlyList<SearchIndexDocument> MergeByRecord(IEnumerable<SearchIndexDocument> ordered)
    {
        var merged = new Dictionary<(Guid, string?), SearchIndexDocument>();
        foreach (var d in ordered)
        {
            var key = (d.PublicId, d.CustomIndexName);
            if (!merged.TryGetValue(key, out var existing)) { merged[key] = d; continue; }

            var values = new Dictionary<long, object?>(existing.Values);
            foreach (var kv in d.Values) values[kv.Key] = kv.Value;

            Dictionary<string, object?>? slots = null;
            if (existing.SlotValues != null || d.SlotValues != null)
            {
                slots = new Dictionary<string, object?>(existing.SlotValues ?? new Dictionary<string, object?>());
                foreach (var kv in d.SlotValues ?? new Dictionary<string, object?>()) slots[kv.Key] = kv.Value;
            }

            var version = existing.SourceVersion.HasValue && d.SourceVersion.HasValue
                ? Math.Max(existing.SourceVersion.Value, d.SourceVersion.Value)
                : existing.SourceVersion ?? d.SourceVersion;

            merged[key] = d with { Values = values, SlotValues = slots, SourceVersion = version };
        }
        return merged.Values.ToList();
    }

    /// <summary>
    /// Whether a message must be answered from the database instead of from its own payload. A payload is a snapshot taken at
    /// commit time. It is trustworthy only when this is the first delivery and nothing newer has reached the index; a
    /// redelivered message, or one older than what the index already holds, may overwrite newer values with old ones.
    /// </summary>
    public static bool NeedsAuthoritativeRead(bool hasPayload, int deliveryCount, long messageVersion, long? indexedVersion) =>
        !hasPayload || deliveryCount > 1 || (indexedVersion.HasValue && messageVersion < indexedVersion.Value);
}
