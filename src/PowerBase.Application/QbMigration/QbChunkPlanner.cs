namespace PowerBase.Application.QbMigration;

/// <summary>A range of QuickBase Record IDs: <see cref="From"/> inclusive, <see cref="To"/> exclusive.</summary>
public sealed record QbRange(long From, long To)
{
    /// <summary>The QuickBase query for exactly this range.</summary>
    public string Where => $"{{{QbSystemFids.RecordId}.GTE.{From}}}AND{{{QbSystemFids.RecordId}.LT.{To}}}";
}

/// <summary>Splits a table into Record ID ranges. Ranges (not skip offsets) keep every chunk stable while QuickBase
/// is still live: a record added or deleted elsewhere never shifts another chunk's rows, and a chunk can be read
/// again and give the same records.</summary>
public static class QbChunkPlanner
{
    /// <param name="minId">Smallest Record ID in the table.</param>
    /// <param name="maxId">Largest Record ID in the table.</param>
    /// <param name="count">Number of records (fewer than the id span when ids have gaps).</param>
    /// <param name="rowsPerChunk">Target rows per chunk; gaps make real chunks smaller, never larger than the id width.</param>
    public static IReadOnlyList<QbRange> Plan(long minId, long maxId, long count, int rowsPerChunk)
    {
        if (count <= 0) return [];
        if (minId < 1 || maxId < minId) throw new ArgumentException($"Bad Record ID range {minId}..{maxId}.");
        if (rowsPerChunk < 1) throw new ArgumentOutOfRangeException(nameof(rowsPerChunk));

        // Spread the target over the id span by density, so a table with many gaps still gets chunks of about the target size.
        var span = maxId - minId + 1;
        var width = (long)Math.Max(1, Math.Floor((double)span * rowsPerChunk / count));
        var ranges = new List<QbRange>();
        for (var from = minId; from <= maxId; from += width)
            ranges.Add(new QbRange(from, Math.Min(from + width, maxId + 1)));
        return ranges;
    }

    /// <summary>Splits a range in two, used when QuickBase times out on it twice (QuickBase's advice: halve the query).</summary>
    public static (QbRange First, QbRange Second)? Halve(QbRange range)
    {
        if (range.To - range.From < 2) return null;
        var mid = range.From + (range.To - range.From) / 2;
        return (new QbRange(range.From, mid), new QbRange(mid, range.To));
    }
}
