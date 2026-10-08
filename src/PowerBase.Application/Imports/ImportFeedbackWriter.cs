using System.Globalization;
using System.Text;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Imports;

/// <summary>A source row that was not imported, with the values it carried so the user can fix and retry it.</summary>
/// <param name="Table">When an import fills several tables: the table the row was meant for.</param>
/// <param name="ColumnLabel">When an import fills several tables: the name of the field the issue refers to (field ids repeat across tables).</param>
/// <param name="RecordId">When the row matched an existing record (a merge): its Record ID#, even though the row was not imported.</param>
public sealed record ImportFeedbackRow(ImportRunIssue Issue, IReadOnlyList<string?> SourceValues, string? Table = null, string? ColumnLabel = null, long? RecordId = null);

/// <summary>Writes the feedback file: one CSV line for every source row that was not imported, with the reason, the
/// column, and the record it clashes with. Lines go to a temporary file as the run goes, so a million rejected rows
/// never sit in memory; the file is uploaded once at the end and the temporary copy is always deleted.</summary>
public sealed class ImportFeedbackWriter : IAsyncDisposable
{
    private static readonly IReadOnlyDictionary<string, string> ReasonText = new Dictionary<string, string>
    {
        [ImportReason.RequiredMissing] = "Required value missing",
        [ImportReason.RequiredBlank] = "Blank value (Require field)",
        [ImportReason.DuplicateInDestination] = "Already exists",
        [ImportReason.DuplicateInRun] = "Duplicate in this import",
        [ImportReason.TypeMismatch] = "Invalid value",
        [ImportReason.FormatViolation] = "Invalid format",
        [ImportReason.FormulaError] = "Formula could not be calculated",
        [ImportReason.ConstraintViolation] = "Rule violation",
        [ImportReason.WriteFailed] = "Could not be saved",
        [ImportReason.MergeKeyMissing] = "Match key missing",
        [ImportReason.NoChanges] = "Nothing to update",
    };

    /// <summary>The words a person is shown for a reason code.</summary>
    public static string ReasonOf(string code) => ReasonText.GetValueOrDefault(code, code);

    private readonly IReadOnlyList<string> _valueHeaders;
    private readonly IReadOnlyDictionary<int, string> _columnLabels;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"import-feedback-{Guid.NewGuid():N}.csv");
    private readonly bool _includeTable;
    private StreamWriter? _writer;

    /// <param name="valueHeaders">One header per carried source value (the destination field each is mapped to).</param>
    /// <param name="columnLabels">Destination field names by Fid, to name the column an issue refers to.</param>
    /// <param name="includeTable">Several tables are being filled: each line starts with the table it was meant for.</param>
    public ImportFeedbackWriter(IReadOnlyList<string> valueHeaders, IReadOnlyDictionary<int, string> columnLabels, bool includeTable = false)
    {
        _valueHeaders = valueHeaders;
        _columnLabels = columnLabels;
        _includeTable = includeTable;
    }

    public long RowCount { get; private set; }

    public async Task AppendAsync(IReadOnlyList<ImportFeedbackRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        if (_writer is null)
        {
            _writer = new StreamWriter(new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)); // the BOM makes Excel read it as UTF-8
            var headers = new[] { "Source row", "Result", "Reason", "Column", "Details", "Existing record" }.Concat(_valueHeaders);
            await _writer.WriteLineAsync(string.Join(',', (_includeTable ? new[] { "Table" }.Concat(headers) : headers).Select(Cell)));
        }
        foreach (var row in rows)
        {
            var i = row.Issue;
            var cells = new List<string?>();
            if (_includeTable) cells.Add(row.Table);
            cells.AddRange(new string?[]
            {
                i.SourceRowRef?.ToString(CultureInfo.InvariantCulture),
                i.Outcome == ImportOutcome.Skipped ? "Skipped" : "Error",
                ReasonText.GetValueOrDefault(i.ReasonCode, i.ReasonCode),
                row.ColumnLabel ?? (i.ColumnFid is { } fid && _columnLabels.TryGetValue(fid, out var label) ? label : null),
                i.Message,
                i.ExistingRecordRef?.ToString(CultureInfo.InvariantCulture),
            });
            cells.AddRange(row.SourceValues);
            await _writer.WriteLineAsync(string.Join(',', cells.Select(Cell)));
        }
        RowCount += rows.Count;
    }

    /// <summary>Uploads the file and returns its stored path, or null when no row was written. The name is unguessable
    /// and the path is never sent to clients: downloads go through an authorised endpoint.</summary>
    public async Task<string?> SaveAsync(IFileStorageService storage, Guid runPublicId, CancellationToken ct)
    {
        if (_writer is null) return null;
        await _writer.FlushAsync(ct);
        await _writer.DisposeAsync();
        _writer = null;
        await using var content = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        var stored = await storage.SaveAsync(content, "import-feedback.csv", "text/csv", ct,
            uniqueKey: $"import-feedback-{runPublicId:N}-{Guid.NewGuid():N}");
        return stored.Path;
    }

    public async ValueTask DisposeAsync()
    {
        if (_writer is not null) { await _writer.DisposeAsync(); _writer = null; }
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch (IOException) { /* best effort: the temp folder is cleaned by the OS */ }
    }

    /// <summary>CSV-quotes a cell. Text a spreadsheet would run as a formula (starting with = + - @) is prefixed with an
    /// apostrophe so a hostile source value cannot execute when the file is opened in Excel.</summary>
    public static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value[0] is '=' or '+' or '@' or '\t' or '\r'
            || (value[0] == '-' && !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _)))
            value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }

    /// <summary>The text of a source value as it should appear in the file.</summary>
    public static string? Format(object? value) => value switch
    {
        null or DBNull => null,
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };
}
