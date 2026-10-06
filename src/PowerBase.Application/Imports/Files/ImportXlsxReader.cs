using System.Globalization;
using System.IO.Compression;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace PowerBase.Application.Imports.Files;

/// <summary>Streams one sheet of an .xlsx file a row at a time, so a workbook of hundreds of thousands of rows is read without loading
/// the sheet into memory. Every cell is returned as text: numbers in plain invariant form, dates and times as ISO text (the cell's
/// number format decides which cells are dates), checkboxes as true/false. Formula cells give the value last calculated by Excel.</summary>
public sealed class ImportXlsxReader : IImportRowReader
{
    private readonly SpreadsheetDocument _document;
    private readonly OpenXmlReader _reader;
    private readonly string[] _sharedStrings;
    private readonly bool[] _dateStyles;
    private long _rows;

    private ImportXlsxReader(SpreadsheetDocument document, WorksheetPart sheet)
    {
        _document = document;
        _sharedStrings = LoadSharedStrings(document);
        _dateStyles = LoadDateStyles(document);
        _reader = OpenXmlReader.Create(sheet);
    }

    /// <summary>The names of the sheets, in workbook order (hidden ones included: the person may still pick them).</summary>
    public static IReadOnlyList<string> SheetNames(Stream stream)
    {
        using var document = Open(stream);
        var workbook = document.WorkbookPart;
        // Only sheets of cells: a chart sheet has nothing to read.
        return workbook?.Workbook.Sheets?.Elements<Sheet>()
            .Where(s => s.Id?.Value is { } id && workbook.GetPartById(id) is WorksheetPart)
            .Select(s => s.Name?.Value ?? "").Where(n => n.Length > 0).ToList() ?? [];
    }

    public static ImportXlsxReader Open(Stream stream, string? sheetName)
    {
        var document = Open(stream);
        try
        {
            var workbook = document.WorkbookPart ?? throw new ImportFileFormatException("The workbook has no sheets.");
            var sheets = workbook.Workbook.Sheets?.Elements<Sheet>().ToList() ?? [];
            var sheet = string.IsNullOrWhiteSpace(sheetName)
                ? sheets.FirstOrDefault()
                : sheets.FirstOrDefault(s => string.Equals(s.Name?.Value, sheetName, StringComparison.OrdinalIgnoreCase));
            if (sheet?.Id?.Value is not { } relationship)
                throw new ImportFileFormatException(string.IsNullOrWhiteSpace(sheetName) ? "The workbook has no sheets." : $"The sheet '{sheetName}' is not in the file.");
            return new ImportXlsxReader(document, (WorksheetPart)workbook.GetPartById(relationship));
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    /// <summary>A workbook is a zip, and a small zip can hold gigabytes: a file made to exhaust the server's disk and time while it is
    /// "read". The sizes the zip declares are checked before anything is unpacked: a real workbook compresses text roughly 5 to 20
    /// times, so a part of any size that claims more than <see cref="MaxCompressionRatio"/> times, or a total beyond
    /// <see cref="MaxUncompressedBytes"/>, is refused.</summary>
    public const long MaxUncompressedBytes = 4L * 1024 * 1024 * 1024;
    public const int MaxCompressionRatio = 100;
    public const long RatioCheckFloor = 20L * 1024 * 1024;

    public static void GuardArchive(Stream stream)
    {
        var start = stream.CanSeek ? stream.Position : 0;
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                total += entry.Length;
                var ratio = entry.CompressedLength == 0 ? double.MaxValue : (double)entry.Length / entry.CompressedLength;
                if (total > MaxUncompressedBytes || (entry.Length > RatioCheckFloor && ratio > MaxCompressionRatio))
                    throw new ImportFileFormatException("The workbook is not a normal Excel file: it expands to far more than it should. Save it again as .xlsx or .csv.");
            }
        }
        catch (InvalidDataException)
        {
            throw new ImportFileFormatException("The file could not be read as an Excel workbook. Save it again as .xlsx, or as .csv.");
        }
        finally
        {
            if (stream.CanSeek) stream.Position = start;
        }
    }

    private static SpreadsheetDocument Open(Stream stream)
    {
        GuardArchive(stream);
        try { return SpreadsheetDocument.Open(stream, false); }
        catch (Exception ex) when (ex is OpenXmlPackageException or InvalidDataException or FileFormatException or IOException or System.Xml.XmlException)
        {
            throw new ImportFileFormatException("The file could not be read as an Excel workbook. Save it again as .xlsx, or as .csv.");
        }
    }

    public bool Read(out long rowNumber, out string?[] cells)
    {
        rowNumber = 0;
        cells = [];
        while (_reader.Read())
        {
            if (_reader.ElementType != typeof(Row) || !_reader.IsStartElement) continue;
            var row = (Row)_reader.LoadCurrentElement()!;
            if (++_rows > ImportFileFormats.MaxRows) throw new ImportFileFormatException($"The sheet has more than {ImportFileFormats.MaxRows:N0} rows. Split it into smaller files.");
            rowNumber = row.RowIndex?.Value ?? _rows;
            cells = ReadCells(row, rowNumber);
            return true;
        }
        return false;
    }

    private string?[] ReadCells(Row row, long rowNumber)
    {
        var values = new List<string?>();
        var next = 0;
        foreach (var cell in row.Elements<Cell>())
        {
            // Cells that are empty are not stored, so the column comes from the cell's own address (B7 -> 1).
            var column = cell.CellReference?.Value is { } address ? ColumnIndex(address) : next;
            if (column >= ImportFileFormats.MaxColumns) throw new ImportFileFormatException($"Row {rowNumber} has cells beyond column {ImportFileFormats.MaxColumns}.");
            while (values.Count < column) values.Add(null);
            if (values.Count == column) values.Add(Text(cell));
            else values[column] = Text(cell);
            next = column + 1;
        }
        return values.ToArray();
    }

    private string? Text(Cell cell)
    {
        string? raw = cell.CellValue?.Text;
        string? text;
        var type = cell.DataType?.Value;
        if (type == CellValues.SharedString)
            text = int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < _sharedStrings.Length ? _sharedStrings[index] : null;
        else if (type == CellValues.InlineString) text = cell.InlineString?.InnerText;
        else if (type == CellValues.Boolean) text = raw == "1" ? "true" : "false";
        else if (type is null || type == CellValues.Number) text = Number(raw, cell.StyleIndex?.Value ?? 0);
        else text = raw; // str (formula text), error codes, ISO dates (d)
        if (text is { Length: > ImportFileFormats.MaxCellLength }) throw new ImportFileFormatException("A cell holds more than 100,000 characters.");
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private string? Number(string? raw, uint style)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return raw;
        if (style < _dateStyles.Length && _dateStyles[style])
        {
            // Dates are counted in days since 1900; a value outside that range is not a date, so it stays a number.
            if (value is >= 0 and < 2_958_466)
            {
                var date = DateTime.FromOADate(value);
                return date.TimeOfDay == TimeSpan.Zero ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : date.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            }
        }
        // Plain decimal form: no exponent, no trailing zeros, so 1E-05 reads as 0.00001 and 1234.50 as 1234.5.
        return decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var exact)
            ? exact.ToString("0.#############################", CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>"A" = 0, "Z" = 25, "AA" = 26, from the letters of a cell address such as "AB12".</summary>
    public static int ColumnIndex(string address)
    {
        var index = 0;
        foreach (var c in address)
        {
            if (c is < 'A' or > 'Z') { if (c is >= 'a' and <= 'z') { index = index * 26 + (c - 'a' + 1); continue; } break; }
            index = index * 26 + (c - 'A' + 1);
        }
        return index - 1;
    }

    private static string[] LoadSharedStrings(SpreadsheetDocument document)
    {
        var part = document.WorkbookPart?.SharedStringTablePart;
        if (part is null) return [];
        var strings = new List<string>();
        using var reader = OpenXmlReader.Create(part);
        while (reader.Read())
        {
            if (reader.ElementType == typeof(SharedStringItem) && reader.IsStartElement)
            {
                strings.Add(((SharedStringItem)reader.LoadCurrentElement()!).InnerText);
                if (strings.Count > 20_000_000) throw new ImportFileFormatException("The workbook has too many distinct text values.");
            }
        }
        return strings.ToArray();
    }

    /// <summary>For each cell style, whether its number format shows a date or time (built-in date formats, or a custom one with
    /// day / month / year / hour / second codes outside quotes and brackets).</summary>
    private static bool[] LoadDateStyles(SpreadsheetDocument document)
    {
        var styles = document.WorkbookPart?.WorkbookStylesPart?.Stylesheet;
        var formats = styles?.CellFormats?.Elements<CellFormat>().ToList();
        if (formats is null) return [];
        var custom = styles!.NumberingFormats?.Elements<NumberingFormat>()
            .Where(n => n.NumberFormatId?.Value is not null)
            .ToDictionary(n => n.NumberFormatId!.Value, n => n.FormatCode?.Value ?? "") ?? new Dictionary<uint, string>();
        return formats.Select(f =>
        {
            var id = f.NumberFormatId?.Value ?? 0;
            if (custom.TryGetValue(id, out var code)) return LooksLikeDate(code);
            return id is (>= 14 and <= 22) or (>= 27 and <= 36) or (>= 45 and <= 47) or (>= 50 and <= 58);
        }).ToArray();
    }

    internal static bool LooksLikeDate(string formatCode)
    {
        var inQuote = false;
        var inBracket = false;
        foreach (var c in formatCode)
        {
            if (c == '"') inQuote = !inQuote;
            else if (inQuote) continue;
            else if (c == '[') inBracket = true;
            else if (c == ']') inBracket = false;
            else if (!inBracket && char.ToLowerInvariant(c) is 'y' or 'd' or 'h' or 's') return true;
            else if (!inBracket && char.ToLowerInvariant(c) == 'm') return true;
        }
        return false;
    }

    public void Dispose()
    {
        _reader.Dispose();
        _document.Dispose();
    }
}
