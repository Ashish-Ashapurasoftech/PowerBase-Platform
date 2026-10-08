using System.Globalization;
using System.Text;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Imports;

/// <summary>Whether a value already stored in a record is the value an import would write. Only answers "same" when it is certain: a value
/// of a kind that is not plainly comparable is different, because writing it again is harmless and not writing a real change is not.</summary>
public static class ImportValueEquality
{
    public static bool Same(object? stored, object? incoming)
    {
        var storedBlank = IsBlank(stored);
        var incomingBlank = IsBlank(incoming);
        if (storedBlank || incomingBlank) return storedBlank && incomingBlank;

        if (IsNumber(stored) && IsNumber(incoming)) return ToDecimal(stored!) == ToDecimal(incoming!);
        if (stored is bool a && incoming is bool b) return a == b;
        if (stored is DateTime d1 && incoming is DateTime d2) return d1 == d2;
        if (stored is string s1 && incoming is string s2) return string.Equals(s1, s2, StringComparison.Ordinal);
        return false;
    }

    private static bool IsBlank(object? v) => v is null or DBNull || (v is string s && s.Length == 0);

    private static bool IsNumber(object? v) => v is byte or short or int or long or float or double or decimal;

    private static decimal ToDecimal(object v)
    {
        try { return Convert.ToDecimal(v, CultureInfo.InvariantCulture); }
        catch (OverflowException) { return decimal.MaxValue; }
    }
}

/// <summary>The details file of one destination table for one run: a line for every source row the run gave that table, whatever happened to
/// it (inserted, updated, left unchanged, skipped or an error), with the Record ID# of the record it became or matched, the reason when it was
/// not imported, and the values it carried. Lines go to a temporary file as the run goes, so a million rows never sit in memory; the file is
/// uploaded once at the end and the temporary copy is always deleted. A run that gave the table no rows still has a file, with its headings.</summary>
public sealed class ImportDetailsWriter : IAsyncDisposable
{
    private readonly IReadOnlyList<string> _valueHeaders;
    private readonly IReadOnlyDictionary<int, string> _columnLabels;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"import-details-{Guid.NewGuid():N}.csv");
    private StreamWriter? _writer;

    /// <param name="valueHeaders">One heading per carried value (the destination field each is mapped to).</param>
    /// <param name="columnLabels">Destination field names by id, to name the column a problem refers to.</param>
    public ImportDetailsWriter(IReadOnlyList<string> valueHeaders, IReadOnlyDictionary<int, string> columnLabels)
    {
        _valueHeaders = valueHeaders;
        _columnLabels = columnLabels;
    }

    public long RowCount { get; private set; }

    public static readonly string[] FixedHeaders = ["Source row", "Result", "Record ID#", "Reason", "Column", "Details", "Existing record"];

    public async Task AppendAsync(IReadOnlyList<ImportFeedbackRow> problems, IReadOnlyList<ImportWrittenRow>? written, CancellationToken ct)
    {
        var writer = await WriterAsync();
        // One line per source row, in the order of the source.
        var lines = new List<(long Row, string Line)>(problems.Count + (written?.Count ?? 0));
        foreach (var w in written ?? [])
            lines.Add((w.SourceRowRef, Line(w.SourceRowRef, w.Outcome switch
            {
                ImportOutcome.Inserted => "Inserted",
                ImportOutcome.Updated => "Updated",
                _ => "Unchanged"
            }, w.RecordId, null, null, w.Note, null, w.SourceValues)));
        foreach (var p in problems)
        {
            var i = p.Issue;
            var column = p.ColumnLabel ?? (i.ColumnFid is { } fid && _columnLabels.TryGetValue(fid, out var label) ? label : null);
            lines.Add((i.SourceRowRef ?? 0, Line(i.SourceRowRef ?? 0, i.Outcome == ImportOutcome.Skipped ? "Skipped" : "Error", p.RecordId,
                ImportFeedbackWriter.ReasonOf(i.ReasonCode), column, i.Message, i.ExistingRecordRef, p.SourceValues)));
        }
        lines.Sort((a, b) => a.Row.CompareTo(b.Row)); // a stable sort in effect: rows with the same number keep their order
        foreach (var (_, line) in lines) await writer.WriteLineAsync(line);
        RowCount += lines.Count;
    }

    private static string Line(long row, string result, long? recordId, string? reason, string? column, string? details, long? existing, IReadOnlyList<string?> values)
    {
        var cells = new List<string?>
        {
            row == 0 ? null : row.ToString(CultureInfo.InvariantCulture), result, recordId?.ToString(CultureInfo.InvariantCulture),
            reason, column, details, existing?.ToString(CultureInfo.InvariantCulture)
        };
        cells.AddRange(values);
        return string.Join(',', cells.Select(ImportFeedbackWriter.Cell));
    }

    private async Task<StreamWriter> WriterAsync()
    {
        if (_writer is not null) return _writer;
        _writer = new StreamWriter(new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)); // the BOM makes Excel read it as UTF-8
        await _writer.WriteLineAsync(string.Join(',', FixedHeaders.Concat(_valueHeaders).Select(ImportFeedbackWriter.Cell)));
        return _writer;
    }

    /// <summary>Uploads the file and returns its stored path. The name is unguessable and the path is never sent to clients: downloads go
    /// through an authorised endpoint.</summary>
    public async Task<string> SaveAsync(IFileStorageService storage, Guid runPublicId, int tableIndex, CancellationToken ct)
    {
        var writer = await WriterAsync();
        await writer.FlushAsync(ct);
        await writer.DisposeAsync();
        _writer = null;
        await using var content = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        var stored = await storage.SaveAsync(content, "import-details.csv", "text/csv", ct,
            uniqueKey: $"import-details-{runPublicId:N}-{tableIndex}-{Guid.NewGuid():N}");
        return stored.Path;
    }

    public async ValueTask DisposeAsync()
    {
        if (_writer is not null) { await _writer.DisposeAsync(); _writer = null; }
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch (IOException) { /* best effort: the temp folder is cleaned by the OS */ }
    }
}
