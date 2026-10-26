using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Search;

/// <summary>
/// Pushes one field's values into the index after the field is switched on or off for search (or restored). The index
/// column comes from the field's stored slot, so it is the same column the indexer worker writes and the report queries
/// read, and no other field's column can move because of this change.
/// </summary>
public static class FieldSlotSync
{
    /// <param name="Slot">The index column holding the field, or null when it has none (no slot free, or not allocated yet).</param>
    public sealed record Plan(string? Slot, string? TypeCode, string? TargetIndex, IReadOnlySet<string> SearchTextSlots);

    public static async Task<Plan> PlanAsync(IAppFieldRepository fieldRepo, long tableId, int fid, bool isNullify, CancellationToken ct)
    {
        // A field that has just become eligible has no slot yet: give it one before anything is written.
        await fieldRepo.EnsureSearchSlotsAsync(tableId, ct);
        var all = await fieldRepo.ListByTableAsync(tableId, ct);

        var isWide = SearchSlotResolver.IsWideTable(all);
        var field = all.FirstOrDefault(f => f.Fid == fid);

        string? slot = null;
        if (field != null)
        {
            if (isWide)
                slot = SearchSlotResolver.IsSearchEligible(field) || isNullify ? $"f_{fid}" : null;
            else if (!string.IsNullOrWhiteSpace(field.SearchSlot) && (isNullify || SearchSlotResolver.IsSearchEligible(field)))
                slot = field.SearchSlot; // switching off keeps its stored slot, which is exactly the column to clear
        }

        var map = SearchSlotResolver.BuildSlotMap(all);
        var searchText = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in all)
        {
            if (!f.Fid.HasValue || !f.IsSearchable || !SearchSlotResolver.IsSearchEligible(f)) continue;
            var name = map[(long)f.Fid.Value];
            if (isWide || !name.StartsWith("f_")) searchText.Add(name);
        }

        return new Plan(slot, field?.TypeCode, isWide ? SearchSlotResolver.GetTableIndexName(tableId) : null, searchText);
    }

    /// <summary>The documents to upload: each record's value for the field in the field's column (null when switching off).</summary>
    public static IReadOnlyList<SearchIndexDocument> ToDocuments(IEnumerable<SearchIndexDocument> docs, Plan plan, int fid, bool isNullify)
    {
        if (plan.Slot == null) return [];
        return docs.Select(d =>
        {
            var slotVals = new Dictionary<string, object?>();
            if (isNullify) slotVals[plan.Slot] = null;
            else if (d.Values.TryGetValue(fid, out var val)) slotVals[plan.Slot] = SearchSlotResolver.ToSlotValue(plan.TypeCode, val);
            return new SearchIndexDocument(d.TenantId, d.AppId, d.TableId, d.PublicId, d.Values, slotVals, plan.TargetIndex, plan.SearchTextSlots);
        }).ToList();
    }
}
