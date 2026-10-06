using System.Globalization;
using System.Text;

namespace PowerBase.Application.Imports.Files;

public static class ImportFileFormats
{
    public const string Csv = "csv";
    public const string Xlsx = "xlsx";

    /// <summary>Largest file a person may upload. The files are read as streams, so this is a limit on disk and time, not memory.</summary>
    public const long MaxFileBytes = 300L * 1024 * 1024;
    public const long MaxRows = 5_000_000;
    public const int MaxColumns = 1000;
    public const int MaxCellLength = 100_000;

    /// <summary>The format a file is read as, or null when it is not one this import reads. Decided by what is inside the file as
    /// well as its name, so a renamed file is read as what it really is.</summary>
    public static string? Detect(string fileName, ReadOnlySpan<byte> head)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var zip = head.Length >= 4 && head[0] == 0x50 && head[1] == 0x4B && head[2] is 0x03 or 0x05;
        if (zip) return ext is ".xlsx" or ".xlsm" ? Xlsx : null;
        // The old binary Excel format and other containers start with these bytes; they are not text and not xlsx.
        if (head.Length >= 4 && head[0] == 0xD0 && head[1] == 0xCF && head[2] == 0x11 && head[3] == 0xE0) return null;
        return ext is ".csv" or ".txt" or ".tsv" ? Csv : null;
    }
}

/// <summary>The row that should hold the column names is not in the file (an empty sheet, or a header row below the last row).</summary>
public sealed class ImportFileHeaderMissingException(string message) : ImportFileFormatException(message);

/// <summary>The file cannot be read as a table (damaged, wrong format, or larger than the limits). The message says what to fix.</summary>
public class ImportFileFormatException(string message) : Exception(message);

/// <summary>Reads a file one row at a time, whatever its format, without holding more than the current row in memory. A row is the
/// cells of one line (CSV record, spreadsheet row) as text; an empty cell is null.</summary>
public interface IImportRowReader : IDisposable
{
    /// <summary>The next row, or false at the end. <paramref name="rowNumber"/> is the row's number as the person sees it: the
    /// spreadsheet row, or the record's position in a CSV (the first line is 1).</summary>
    bool Read(out long rowNumber, out string?[] cells);
}

/// <summary>A CSV (or tab/semicolon/pipe separated) reader following RFC 4180: quoted values may hold the delimiter, line breaks and
/// doubled quotes; the byte order mark is skipped; a value that never closes its quote is reported instead of swallowing the file.</summary>
public sealed class ImportCsvReader : IImportRowReader
{
    private readonly StreamReader _reader;
    private readonly char _delimiter;
    private readonly char[] _buffer = new char[16 * 1024];
    private int _length, _position;
    private long _row;
    private readonly StringBuilder _field = new();
    private readonly List<string?> _cells = new();

    public ImportCsvReader(Stream stream, char delimiter)
    {
        _delimiter = delimiter;
        _reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: true);
    }

    private int Peek()
    {
        if (_position >= _length)
        {
            _length = _reader.Read(_buffer, 0, _buffer.Length);
            _position = 0;
            if (_length == 0) return -1;
        }
        return _buffer[_position];
    }

    private int Next()
    {
        var c = Peek();
        if (c >= 0) _position++;
        return c;
    }

    public bool Read(out long rowNumber, out string?[] cells)
    {
        rowNumber = 0;
        cells = [];
        if (Peek() < 0) return false;
        _row++;
        if (_row > ImportFileFormats.MaxRows) throw new ImportFileFormatException($"The file has more than {ImportFileFormats.MaxRows:N0} rows. Split it into smaller files.");
        _cells.Clear();
        _field.Clear();
        var quoted = false;
        var atFieldStart = true;
        var wasQuotedField = false;

        while (true)
        {
            var c = Next();
            if (c < 0)
            {
                if (quoted) throw new ImportFileFormatException($"Row {_row}: a quoted value is never closed. Check the quotation marks.");
                break;
            }
            if (quoted)
            {
                if (c == '"')
                {
                    if (Peek() == '"') { Next(); _field.Append('"'); }
                    else quoted = false;
                }
                else _field.Append((char)c);
            }
            else if (c == '"' && atFieldStart) { quoted = true; wasQuotedField = true; atFieldStart = false; continue; }
            else if (c == _delimiter) { EndField(ref wasQuotedField); atFieldStart = true; continue; }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && Peek() == '\n') Next();
                break;
            }
            else _field.Append((char)c);

            atFieldStart = false;
            if (_field.Length > ImportFileFormats.MaxCellLength)
                throw new ImportFileFormatException($"Row {_row}: a value is longer than {ImportFileFormats.MaxCellLength:N0} characters. Check the quotation marks.");
        }

        EndField(ref wasQuotedField);
        if (_cells.Count > ImportFileFormats.MaxColumns) throw new ImportFileFormatException($"Row {_row} has more than {ImportFileFormats.MaxColumns} columns.");
        rowNumber = _row;
        cells = _cells.ToArray();
        return true;
    }

    private void EndField(ref bool wasQuoted)
    {
        var text = _field.ToString();
        // An empty or spaces-only cell is a blank; a quoted empty value ("") is blank too.
        _cells.Add(string.IsNullOrWhiteSpace(text) ? null : text);
        _field.Clear();
        wasQuoted = false;
    }

    public void Dispose() => _reader.Dispose();

    /// <summary>Picks the separator from the first lines: whichever of comma, semicolon, tab and pipe splits them into the same
    /// number of (more than one) columns. Quoted text is ignored while counting. Falls back to a comma.</summary>
    public static char DetectDelimiter(Stream stream)
    {
        var start = stream.CanSeek ? stream.Position : 0;
        var sample = new char[32 * 1024];
        int read;
        using (var reader = new StreamReader(stream, new UTF8Encoding(false), true, 32 * 1024, leaveOpen: true))
            read = reader.Read(sample, 0, sample.Length);
        if (stream.CanSeek) stream.Position = start;

        char best = ',';
        var bestScore = 0;
        foreach (var candidate in new[] { ',', ';', '\t', '|' })
        {
            var counts = new List<int>();
            var count = 0;
            var quoted = false;
            for (var i = 0; i < read && counts.Count < 10; i++)
            {
                var c = sample[i];
                if (c == '"') quoted = !quoted;
                else if (!quoted && c == candidate) count++;
                else if (!quoted && c == '\n') { counts.Add(count); count = 0; }
            }
            if (counts.Count == 0 || counts[0] == 0) continue;
            var consistent = counts.Count(n => n == counts[0]);
            var score = consistent * 1000 + counts[0];
            if (score > bestScore) { bestScore = score; best = candidate; }
        }
        return best;
    }
}
