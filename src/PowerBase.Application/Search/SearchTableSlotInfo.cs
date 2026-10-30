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
    IReadOnlySet<string>? WideSearchTextSlots,
    IReadOnlySet<long> EligibleFids,
    IReadOnlyList<(long Fid, string? TypeCode)> SearchTextFields)
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
        var eligibleFids = new HashSet<long>();
        // Every Searchable field's text goes into quick search, whatever its type and whether or not it got a column of its own.
        var searchTextFields = new List<(long Fid, string? TypeCode)>();
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
            eligibleFids.Add(fid);
            if (f.IsSearchable && !PowerBase.Domain.Constants.PhysicalNaming.IsComputedTypeCode(f.TypeCode))
                searchTextFields.Add((fid, f.TypeCode));

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
            searchText, wideMap, wideSearchText, eligibleFids, searchTextFields);
    }
}

/// <summary>Turns one record's current values into the index document(s) for its table.</summary>
public static class SearchIndexDocumentBuilder
{
    /// <summary>
    /// One document for the table's current index, plus a second one for its own index while the table is migrating to wide.
    /// <paramref name="values"/> maps field number to the plain (decrypted) value; a field absent from it is left untouched in
    /// the index, a null clears it. Pass <paramref name="complete"/> only when <paramref name="values"/> holds every indexed field
    /// of the record (a database read): the free-text field is then rebuilt from it instead of only ever being added to.
    /// </summary>
    public static IReadOnlyList<SearchIndexDocument> Build(
        SearchTableSlotInfo info, long tenantId, long appId, long tableId, Guid publicId,
        IReadOnlyDictionary<long, object?> values, long? sourceVersion, bool complete = false)
    {
        // The whole record's searchable text, the same for the shared index and the table's own one (numbers and dates included).
        var text = complete ? SearchText.Join(info.SearchTextFields, values) : null;
        var docs = new List<SearchIndexDocument>(2)
        {
            new(tenantId, appId, tableId, publicId, values, Slots(info.SlotMap, values), info.TargetIndex, info.SearchTextSlots, sourceVersion, complete, text)
        };

        if (info.MigratingToWide)
            docs.Add(new SearchIndexDocument(tenantId, appId, tableId, publicId, values, Slots(info.WideSlotMap!, values),
                info.WideIndexName(tableId), info.WideSearchTextSlots, sourceVersion, complete, text));

        return docs;
    }

    /// <summary>A field's index column(s) to empty when its records are rewritten: the field was switched off, deleted, or moved to
    /// another column by a type change. <see cref="Slot"/> is null when only the field's column in its own (wide) index applies.</summary>
    public sealed record FieldColumnClear(long Fid, SlotRef? Slot);

    /// <summary>
    /// Rewrites one record in full after one of its table's fields changed: every indexed value from <paramref name="record"/> (which
    /// must be a full database read), the free-text field rebuilt from the searchable text alone, and the columns in
    /// <paramref name="clear"/> emptied. A cleared column that another field uses now keeps that field's value.
    /// </summary>
    public static IReadOnlyList<SearchIndexDocument> BuildRebuild(SearchTableSlotInfo info, SearchIndexDocument record, FieldColumnClear? clear)
    {
        var docs = Build(info, record.TenantId, record.AppId, record.TableId, record.PublicId, record.Values, null, complete: true);
        if (clear == null) return docs;

        var result = new List<SearchIndexDocument>(docs.Count);
        for (var i = 0; i < docs.Count; i++)
        {
            var slots = new Dictionary<string, object?>();
            if (i == 0)
            {
                if (clear.Slot != null) SearchSlotResolver.AddClearedSlots(slots, clear.Slot);
            }
            else
            {
                // the second document is the table's own index while it is moving there
                slots[SearchSlotResolver.WideSlot((int)clear.Fid)] = null;
                slots[SearchSlotResolver.WideEndSlot((int)clear.Fid)] = null;
            }
            // Values last: a cleared column that a field (this one included) still writes keeps its current value.
            foreach (var kv in docs[i].SlotValues ?? new Dictionary<string, object?>()) slots[kv.Key] = kv.Value;
            result.Add(docs[i] with { SlotValues = slots });
        }
        return result;
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

    /// <summary>Empties <paramref name="columns"/> in the documents bound for <paramref name="currentIndex"/> (the table's own index, while
    /// it moves there, has no such columns), keeping any value a document itself writes into one of them.</summary>
    public static IReadOnlyList<SearchIndexDocument> ClearColumns(
        IReadOnlyList<SearchIndexDocument> docs, string? currentIndex, IReadOnlyCollection<string> columns)
    {
        if (columns.Count == 0) return docs;
        return docs.Select(d =>
        {
            if (!string.Equals(d.CustomIndexName, currentIndex, StringComparison.OrdinalIgnoreCase)) return d;
            var slots = columns.ToDictionary(c => c, _ => (object?)null, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in d.SlotValues ?? new Dictionary<string, object?>()) slots[kv.Key] = kv.Value;
            return d with { SlotValues = slots };
        }).ToList();
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

            // Complete once either part is: the merged slots hold everything the complete one did, with later values on top.
            merged[key] = d with
            {
                Values = values, SlotValues = slots, SourceVersion = version,
                CompleteText = existing.CompleteText || d.CompleteText, SearchText = d.SearchText ?? existing.SearchText
            };
        }
        return merged.Values.ToList();
    }
}
