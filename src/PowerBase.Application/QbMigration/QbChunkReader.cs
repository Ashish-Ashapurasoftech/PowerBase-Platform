namespace PowerBase.Application.QbMigration;

/// <summary>Reads every record of one Record ID range. QuickBase decides how many rows a page holds, so the reader keeps
/// asking (skip += rows received) until it has as many rows as QuickBase said the range has. A shortfall is an error,
/// never a silent partial chunk.</summary>
public sealed class QbChunkReader(IQuickBaseClient client)
{
    /// <summary>Upper bound on pages for one range, so a QuickBase response that never advances can't loop forever.</summary>
    public const int MaxPages = 10_000;

    public async Task<IReadOnlyList<QbRow>> ReadAsync(string tableId, IReadOnlyList<int> select, QbRange range, CancellationToken ct = default)
    {
        var fids = select.Contains(QbSystemFids.RecordId) ? select : [QbSystemFids.RecordId, .. select];
        var rows = new List<QbRow>();
        int? expected = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var result = await client.QueryAsync(tableId, fids, range.Where, rows.Count, null, ct);
            expected ??= result.TotalRecords;
            if (result.TotalRecords != expected)
                throw new QbChunkChangedException(range, expected.Value, result.TotalRecords);

            rows.AddRange(result.Rows);
            if (rows.Count >= expected) break;
            if (result.Rows.Count == 0)
                throw new QbChunkShortException(range, expected.Value, rows.Count);
        }

        if (rows.Count != expected)
            throw new QbChunkShortException(range, expected ?? 0, rows.Count);

        var outside = rows.FirstOrDefault(r => r.RecordId < range.From || r.RecordId >= range.To);
        if (outside is not null)
            throw new InvalidOperationException($"QuickBase returned Record ID {outside.RecordId}, outside the range {range.From}..{range.To - 1}.");
        if (rows.Select(r => r.RecordId).Distinct().Count() != rows.Count)
            throw new InvalidOperationException($"QuickBase returned the same record twice for range {range.From}..{range.To - 1}.");

        return rows.OrderBy(r => r.RecordId).ToList();
    }
}

/// <summary>The range gave fewer rows than QuickBase said it holds. Retryable: the chunk is read again.</summary>
public sealed class QbChunkShortException(QbRange range, int expected, int received)
    : Exception($"Range {range.From}..{range.To - 1}: QuickBase said {expected} records but returned {received}.");

/// <summary>The range's record count changed between pages (records added or deleted in QuickBase meanwhile). Retryable.</summary>
public sealed class QbChunkChangedException(QbRange range, int before, int after)
    : Exception($"Range {range.From}..{range.To - 1} changed while being read ({before} then {after} records).");
