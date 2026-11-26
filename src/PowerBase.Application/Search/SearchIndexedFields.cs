using System.Collections.Generic;
using System.Linq;
using PowerBase.Application.Compute;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Search;

/// <summary>
/// Plan D3 ("use AI while AI can answer"): the fields handed to the search-side builders (OData filter, search order by), with every stored
/// Lookup / Summary / Formula that is fully in the index replaced by its stand-in of the value type — the same stand-in the indexer wrote
/// (it indexes c_{fid} through <see cref="MaterializedReadFields.Substitute"/>), so the slot and its type match. A field not in the index,
/// or with no usable slot, is left as it is, and the builders keep sending conditions on it to SQL. The shown value is not changed here.
/// </summary>
public static class SearchIndexedFields
{
    /// <summary>
    /// <paramref name="fields"/> with the "in AI" ones as stand-ins. <paramref name="forSort"/>: only fields whose every value comes back
    /// unchanged from the index (SearchExact, decision Q2) — a sort on a value the index rounded could order records wrongly.
    /// </summary>
    public static IReadOnlyList<AppField> For(IReadOnlyList<AppField> fields, IReadOnlyDictionary<long, bool> inSearch, bool forSort = false)
    {
        if (inSearch.Count == 0) return fields;
        var ids = Usable(fields, inSearch, forSort);
        return ids.Count == 0 ? fields : MaterializedReadFields.Substitute(fields, ids);
    }

    /// <summary>The Fids of the fields <see cref="For"/> replaces (the computed fields the index can answer for).</summary>
    public static IReadOnlySet<long> Fids(IReadOnlyList<AppField> fields, IReadOnlyDictionary<long, bool> inSearch, bool forSort = false)
    {
        if (inSearch.Count == 0) return new HashSet<long>();
        var ids = Usable(fields, inSearch, forSort);
        return fields.Where(f => f.Fid.HasValue && ids.Contains(f.Id)).Select(f => (long)f.Fid!.Value).ToHashSet();
    }

    private static HashSet<long> Usable(IReadOnlyList<AppField> fields, IReadOnlyDictionary<long, bool> inSearch, bool forSort)
    {
        var candidates = fields.Where(f => inSearch.TryGetValue(f.Id, out var exact) && (!forSort || exact)).ToList();
        if (candidates.Count == 0) return new HashSet<long>();
        // Slots are judged on the list as the indexer sees it (every Ready stand-in), so a wide table stays wide.
        var asIndexed = MaterializedReadFields.Substitute(fields, candidates.Select(f => f.Id).ToHashSet());
        var isWide = SearchSlotResolver.IsWideTable(asIndexed);
        var usable = new HashSet<long>();
        foreach (var f in candidates)
        {
            if (MaterializedReadFields.StandIn(f) is not { } s || !SearchSlotResolver.IsSearchEligible(s)) continue;
            if (!isWide && !SearchSlotResolver.SlotMatchesType(s.SearchSlot, s.TypeCode)) continue;   // no typed slot: the index holds nothing
            if (MaterializedReadFields.IsFullTextStored(f)) continue;   // kept whole in SQL, never indexed
            usable.Add(f.Id);
        }
        return usable;
    }
}
