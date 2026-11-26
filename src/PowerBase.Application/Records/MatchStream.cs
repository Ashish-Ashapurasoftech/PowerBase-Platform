using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Relationships;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Records;

/// <summary>
/// Every record matching a filter that is part SQL and part calculated (Formula / Lookup / Summary without a stored column,
/// or encrypted), streamed in Id batches: SQL applies its part, each batch is projected (relations, then formulas) and the
/// calculated part is applied in memory. Only one batch is in memory at a time, so there is no limit on the table size —
/// what replaces the single read of at most <see cref="InMemoryReadLimit.Rows"/> rows when more records match. The cost
/// grows with the number of rows SQL passes on; the request's cancellation token stops it.
/// </summary>
public static class MatchStream
{
    public const int BatchSize = 5_000;

    public static async IAsyncEnumerable<List<(IReadOnlyDictionary<string, object?> Row, IReadOnlyDictionary<long, object?> Computed)>> MatchesAsync(
        IRecordRepository recordRepo, IRelationalProjector relationalProjector, IFormulaProjector formulaProjector,
        AppTable table, IReadOnlyList<AppField> allFields,
        IReadOnlyList<AppField> readFields, IReadOnlyList<AppField> projectFields, IReadOnlyList<AppField> formulaFields,
        FilterGroup? physicalFilter, FilterGroup? formulaFilter, long? restrictToCreatedBy,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default,
        IReadOnlySet<long>? sqlLookupFids = null,
        int batchSize = BatchSize)
    {
        long afterId = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var batch = await recordRepo.ListBatchAfterIdAsync(table, readFields, afterId, batchSize, physicalFilter,
                restrictToCreatedBy, ct, sqlLookupFids);
            if (batch.Count == 0) yield break;
            afterId = Convert.ToInt64(batch[^1]["Id"], System.Globalization.CultureInfo.InvariantCulture);

            var relational = ReferenceOriginalFallback.Apply(allFields, batch,
                await relationalProjector.ProjectAsync(table, projectFields, batch, ct));
            var computed = formulaProjector.Project(formulaFields, batch, relational, table);
            var pairs = batch.Zip(computed, (r, c) => (Row: r, Computed: c)).ToList();
            if (formulaFilter is not null)
                pairs = FormulaFilterSorter.ApplyFormulaFilters(pairs, formulaFilter, allFields);
            yield return pairs;

            if (batch.Count < batchSize) yield break;
        }
    }

    /// <summary>
    /// Keeps the first <paramref name="keep"/> matches in <paramref name="sorts"/> order while matches stream past: the page a
    /// request shows (and the ones before it) without holding every match. LINQ's sort is stable and batches arrive in Id order,
    /// so ties stay in Id order — the same order an OFFSET read with an Id tiebreak gives.
    /// </summary>
    public static List<(IReadOnlyDictionary<string, object?> Row, IReadOnlyDictionary<long, object?> Computed)> KeepFirst(
        List<(IReadOnlyDictionary<string, object?> Row, IReadOnlyDictionary<long, object?> Computed)> kept,
        List<(IReadOnlyDictionary<string, object?> Row, IReadOnlyDictionary<long, object?> Computed)> batch,
        int keep, IReadOnlyList<SortSpec> sorts, IReadOnlyList<AppField> allFields)
    {
        if (keep <= 0) return kept;
        if (sorts.Count == 0)
        {
            // Id order: the first matches are final as soon as they are seen.
            if (kept.Count >= keep) return kept;
            kept.AddRange(batch.Take(keep - kept.Count));
            return kept;
        }
        var merged = FormulaFilterSorter.ApplySort(kept.Concat(batch), sorts, allFields);
        return merged.Count > keep ? merged.GetRange(0, keep) : merged;
    }
}
