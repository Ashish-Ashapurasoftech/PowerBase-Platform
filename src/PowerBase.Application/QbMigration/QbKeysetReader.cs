using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace PowerBase.Application.QbMigration;

/// <summary>Reads every QuickBase record matching a filter, page by page, continuing after the last Record ID received
/// (<c>{3.GT.last}</c>) instead of skipping a count. Records added, edited or deleted while it reads can't shift a page, so
/// nothing is skipped or read twice. Used by the final sync, which reads while QuickBase is still in use.</summary>
public sealed class QbKeysetReader(IQuickBaseClient client)
{
    public const int MaxPages = 1_000_000;

    /// <param name="filter">QuickBase query clauses joined with AND (no OR), or null for every record.</param>
    public async IAsyncEnumerable<IReadOnlyList<QbRow>> ReadAsync(string tableId, IReadOnlyList<int> select, string? filter,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var fids = select.Contains(QbSystemFids.RecordId) ? select : [QbSystemFids.RecordId, .. select];
        long after = 0;
        for (var page = 0; page < MaxPages; page++)
        {
            var where = filter is null ? $"{{3.GT.{after}}}" : $"{filter}AND{{3.GT.{after}}}";
            var result = await client.QueryAsync(tableId, fids, where, 0, null, ct);
            if (result.Rows.Count == 0) yield break;
            var last = result.Rows.Max(r => r.RecordId);
            if (last <= after) throw new InvalidOperationException($"QuickBase returned Record IDs at or below {after} for a query asking for more.");
            yield return result.Rows;
            // QuickBase said how many records match in total; this page held all that were left.
            if (result.Rows.Count >= result.TotalRecords) yield break;
            after = last;
        }
        throw new InvalidOperationException($"Table {tableId}: more than {MaxPages} pages.");
    }

    /// <summary>Every Record ID of the table, ascending.</summary>
    public async Task<List<long>> RecordIdsAsync(string tableId, CancellationToken ct = default)
    {
        var ids = new List<long>();
        await foreach (var page in ReadAsync(tableId, [QbSystemFids.RecordId], null, ct))
            ids.AddRange(page.Select(r => r.RecordId));
        ids.Sort();
        return ids;
    }

    /// <summary>QuickBase filter for records modified on or after <paramref name="sinceUtc"/>. QuickBase compares Date Modified
    /// by day in the app's time zone, so the filter starts a day early and <see cref="ModifiedSince"/> trims it exactly.</summary>
    public static string ModifiedFilter(DateTime sinceUtc) =>
        $"{{{QbSystemFids.DateModified}.OAF.'{sinceUtc.AddDays(-1):yyyy-MM-dd}'}}";

    /// <summary>True when the row's Date Modified is on or after <paramref name="sinceUtc"/>, or can't be read (then it is
    /// copied again to be safe).</summary>
    public static bool ModifiedSince(QbRow row, DateTime sinceUtc) => ModifiedOn(row) is not { } modified || modified >= sinceUtc;

    /// <summary>The row's Date Modified (UTC), or null when it can't be read.</summary>
    public static DateTime? ModifiedOn(QbRow row)
    {
        if (!row.Values.TryGetValue(QbSystemFids.DateModified, out var v) || v.ValueKind != JsonValueKind.String) return null;
        return DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var modified)
            ? modified : null;
    }

    /// <summary>Date Modified of the given Record IDs that QuickBase still has (null when unreadable), asked 100 at a time.</summary>
    public async Task<Dictionary<long, DateTime?>> ModifiedOnAsync(string tableId, IEnumerable<long> recordIds, CancellationToken ct = default)
    {
        var found = new Dictionary<long, DateTime?>();
        foreach (var part in recordIds.Distinct().Chunk(100))
        {
            var where = string.Join("OR", part.Select(id => $"{{{QbSystemFids.RecordId}.EX.{id}}}"));
            var result = await client.QueryAsync(tableId, [QbSystemFids.RecordId, QbSystemFids.DateModified], where, 0, null, ct);
            if (result.Rows.Count < result.TotalRecords)
                throw new InvalidOperationException($"Table {tableId}: QuickBase returned {result.Rows.Count} of {result.TotalRecords} records asked by id.");
            foreach (var row in result.Rows) found[row.RecordId] = ModifiedOn(row);
        }
        return found;
    }

    /// <summary>The ids in <paramref name="pbIds"/> that are not in <paramref name="qbIds"/>; both ascending.</summary>
    public static List<long> Missing(IReadOnlyList<long> qbIds, IEnumerable<long> pbIds)
    {
        var gone = new List<long>();
        var i = 0;
        foreach (var id in pbIds)
        {
            while (i < qbIds.Count && qbIds[i] < id) i++;
            if (i >= qbIds.Count || qbIds[i] != id) gone.Add(id);
        }
        return gone;
    }
}
