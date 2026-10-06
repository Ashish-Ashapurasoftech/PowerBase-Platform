using PowerBase.Domain.Entities;

namespace PowerBase.Application.Imports.Files;

/// <summary>Where the table is inside a file: which sheet, which row holds the column names (0 = there are none), where the data
/// starts, and for text files the separator.</summary>
public sealed record ImportFileLayout(string Format, string? Sheet, int HeaderRow, int? DataStartRow, char? Delimiter)
{
    public const int MaxHeaderRow = 1000;
    public int FirstDataRow => DataStartRow ?? HeaderRow + 1;
}

/// <summary>One column of a file: its place (1 = first), its name as written, the name it goes by in formulas (a repeated name gets
/// "(2)", "(3)"), and the field id the import engine knows it by.</summary>
public sealed record ImportFileColumn(int Position, string Name, string UniqueName, int Fid, bool Duplicate);

public static class ImportFileColumns
{
    /// <summary>Column fields are numbered from here so they can never be mistaken for a table's own field ids.</summary>
    public const int FidBase = 1000;

    public static int FidOf(int position) => FidBase + position;

    /// <summary>Names the columns from a header row (null = the file has none). A blank name becomes "Column N", and a name that
    /// appears twice keeps its first place: mapping by name always finds the first column of that name.</summary>
    public static IReadOnlyList<ImportFileColumn> FromHeader(string?[]? header, int width)
    {
        var count = Math.Min(Math.Max(width, header?.Length ?? 0), ImportFileFormats.MaxColumns);
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var columns = new List<ImportFileColumn>(count);
        for (var i = 0; i < count; i++)
        {
            var written = (i < (header?.Length ?? 0) ? header![i]?.Trim() : null) ?? "";
            var name = written.Length > 0 ? written : $"Column {i + 1}";
            seen.TryGetValue(name, out var times);
            seen[name] = times + 1;
            var unique = times == 0 ? name : $"{name} ({times + 1})";
            columns.Add(new ImportFileColumn(i + 1, name, unique, FidOf(i + 1), times > 0));
        }
        return columns;
    }

    /// <summary>The fields the import engine reads a file's columns through: all text (a file holds only text; each value is
    /// converted when it is written), plus a "Row number" field standing in for the Record ID# a table would have.</summary>
    public static IReadOnlyList<AppField> ToFields(IReadOnlyList<ImportFileColumn> columns)
    {
        var fields = new List<AppField>(columns.Count + 1)
        {
            new() { Id = -3, AppTableId = 0, Fid = 3, Name = "Row number", Label = "Row number", TypeCode = "Number", IsSystem = true, PhysicalColumnName = "Id" }
        };
        foreach (var c in columns)
            fields.Add(new AppField { Id = -c.Fid, AppTableId = 0, Fid = c.Fid, Name = c.UniqueName, Label = c.UniqueName, TypeCode = "Text" });
        return fields;
    }
}

public sealed record ImportFilePreview(
    IReadOnlyList<ImportFileColumn> Columns, IReadOnlyList<string?[]> Rows, long DataRows, long LastRow, string? Delimiter);

/// <summary>Opens and inspects the table inside a file. Everything streams: a preview of a million-row file reads it once and keeps
/// only the first few rows.</summary>
public static class ImportFileInspector
{
    public const int PreviewRows = 10;

    public static IImportRowReader Open(Stream stream, ImportFileLayout layout) => layout.Format switch
    {
        ImportFileFormats.Xlsx => ImportXlsxReader.Open(stream, layout.Sheet),
        ImportFileFormats.Csv => new ImportCsvReader(stream, layout.Delimiter ?? ImportCsvReader.DetectDelimiter(stream)),
        _ => throw new ImportFileFormatException("This file type cannot be imported. Use .xlsx or .csv.")
    };

    public static bool IsBlank(string?[] cells) => cells.All(c => c is null);

    /// <summary>The columns of the file as it will be read at run time: named from the header row, widened to the widest data row
    /// seen in the first rows so a column without a heading still gets a place.</summary>
    public static IReadOnlyList<ImportFileColumn> ReadColumns(Stream stream, ImportFileLayout layout)
    {
        string?[]? header = null;
        var width = 0;
        var sampled = 0;
        using var reader = Open(stream, layout);
        while (reader.Read(out var rowNumber, out var cells))
        {
            if (layout.HeaderRow > 0 && rowNumber == layout.HeaderRow) { header = cells; width = Math.Max(width, cells.Length); }
            if (rowNumber < layout.FirstDataRow || IsBlank(cells)) continue;
            width = Math.Max(width, cells.Length);
            if (++sampled >= 200) break; // spreadsheets leave trailing empty cells out, so one row's width is not the table's
        }
        if (layout.HeaderRow > 0 && header is null) throw new ImportFileHeaderMissingException($"Row {layout.HeaderRow}, which should hold the column names, is not in the file.");
        return ImportFileColumns.FromHeader(header, width);
    }

    public static ImportFilePreview Preview(Stream stream, ImportFileLayout layout)
    {
        var delimiter = layout.Format == ImportFileFormats.Csv ? layout.Delimiter ?? ImportCsvReader.DetectDelimiter(stream) : (char?)null;
        layout = layout with { Delimiter = delimiter };
        string?[]? header = null;
        var width = 0;
        var rows = new List<string?[]>(PreviewRows);
        long count = 0, last = 0;
        using var reader = Open(stream, layout);
        while (reader.Read(out var rowNumber, out var cells))
        {
            if (layout.HeaderRow > 0 && rowNumber == layout.HeaderRow) header = cells;
            if (rowNumber < layout.FirstDataRow || IsBlank(cells)) continue;
            count++;
            last = rowNumber;
            width = Math.Max(width, cells.Length);
            if (rows.Count < PreviewRows) rows.Add(cells);
        }
        if (layout.HeaderRow > 0 && header is null) throw new ImportFileHeaderMissingException($"Row {layout.HeaderRow}, which should hold the column names, is not in the file.");
        var columns = ImportFileColumns.FromHeader(header, width);
        return new ImportFilePreview(columns, rows, count, last, delimiter is null ? null : delimiter.ToString());
    }
}
