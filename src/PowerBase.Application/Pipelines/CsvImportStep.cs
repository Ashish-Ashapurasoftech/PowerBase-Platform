using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PowerBase.Application.Imports;
using PowerBase.Application.Imports.Files;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pipelines;

/// <summary>One column of the step's Header row and the table field it goes into (empty: the column is skipped).</summary>
public sealed class CsvImportColumnMapping
{
    public string? Column { get; set; }
    public string? Field { get; set; }
}

/// <summary>
/// The saved configuration of the Import with CSV step, shaped like the Quickbase step of the same name: App, Table, Merge field,
/// CSV URL, Row separator, "Is the first row a list of field names?", Header row, one "Field to map to column" per header column and
/// (under Additional fields) Row quote char.
/// </summary>
public sealed class CsvImportDefinition
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString };

    public string? App { get; set; }
    public string? AppId { get; set; }
    public string? AppPublicId { get; set; }
    public string? TableLabel { get; set; }
    public string? TableId { get; set; }
    public string? TablePublicId { get; set; }
    /// <summary>The field id records are matched on: a row whose value matches a record updates it, any other row is added.</summary>
    public string? MergeField { get; set; }
    /// <summary>The URL of the CSV (may hold references to earlier steps), or a file from an earlier step.</summary>
    public string? CsvUrl { get; set; }
    /// <summary>"," (Comma, the default), ";" (Semicolon) or "\t" (Tab).</summary>
    public string? RowSeparator { get; set; }
    /// <summary>"\"" (Double quote, the default), "'" (Single quote) or "|" (Pipe).</summary>
    public string? RowQuoteChar { get; set; }
    /// <summary>The first row of the CSV holds column names: it is left out, never imported.</summary>
    public JsonElement SkipFirstLine { get; set; }
    /// <summary>Comma-separated column names; their order is the order of the columns in the CSV.</summary>
    public string? HeaderRow { get; set; }
    /// <summary>The field each column goes into, by column name: { "Name": "fid_6" } (a list of { column, field } is read too).</summary>
    public JsonElement ColumnMappings { get; set; }

    /// <summary>The column mappings, whichever shape they were saved in.</summary>
    public IReadOnlyList<CsvImportColumnMapping> Mappings
    {
        get
        {
            static string? Text(JsonElement e) => e.ValueKind switch
            {
                JsonValueKind.String => e.GetString(),
                JsonValueKind.Number => e.GetRawText(),
                _ => null
            };
            return ColumnMappings.ValueKind switch
            {
                JsonValueKind.Object => ColumnMappings.EnumerateObject().Select(p => new CsvImportColumnMapping { Column = p.Name, Field = Text(p.Value) }).ToList(),
                JsonValueKind.Array => ColumnMappings.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)
                    .Select(e => new CsvImportColumnMapping
                    {
                        Column = e.EnumerateObject().FirstOrDefault(p => p.NameEquals("column") || p.NameEquals("Column")).Value is var c ? Text(c) : null,
                        Field = e.EnumerateObject().FirstOrDefault(p => p.NameEquals("field") || p.NameEquals("Field")).Value is var f ? Text(f) : null
                    }).ToList(),
                _ => []
            };
        }
    }

    public static CsvImportDefinition Read(string? json)
    {
        try
        {
            return JsonSerializer.Deserialize<CsvImportDefinition>(string.IsNullOrWhiteSpace(json) ? "{}" : json, Options) ?? new();
        }
        catch (JsonException)
        {
            throw new PipelineNonRetryableException("The Import with CSV configuration is invalid.");
        }
    }

    public Guid AppGuid => ParseId(AppPublicId ?? App ?? AppId) ?? throw Required("app");
    public Guid TableGuid => ParseId(TablePublicId ?? TableLabel ?? TableId) ?? throw Required("table");

    public bool FirstRowIsFieldNames => SkipFirstLine.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.String => bool.TryParse(SkipFirstLine.GetString(), out var b) ? b : string.Equals(SkipFirstLine.GetString(), "yes", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    /// <summary>The separator character: Comma unless Semicolon or Tab was chosen.</summary>
    public char Delimiter => RowSeparator switch
    {
        ";" => ';',
        "\t" or "\\t" => '\t',
        _ => ','
    };

    /// <summary>The quote character: Double quote unless Single quote or Pipe was chosen.</summary>
    public char QuoteChar => RowQuoteChar switch
    {
        "'" => '\'',
        "|" => '|',
        _ => '"'
    };

    /// <summary>The names of the Header row, in order (a blank name keeps its place, so later columns keep theirs).</summary>
    /// <remarks>Quickbase splits the Header row with the chosen Row separator, so a Semicolon or Tab step needs its Header row written that way too.</remarks>
    public static IReadOnlyList<string> HeaderNames(string? headerRow, char separator = ',') =>
        string.IsNullOrWhiteSpace(headerRow) ? [] : headerRow.Split(separator).Select(n => n.Trim()).ToList();

    /// <summary>Checked before anything runs, with the messages Quickbase gives a step missing a required option.</summary>
    public void Validate()
    {
        var missing = new List<string>();
        if (ParseId(AppPublicId ?? App ?? AppId) is null) missing.Add("app");
        if (ParseId(TablePublicId ?? TableLabel ?? TableId) is null) missing.Add("table");
        if (string.IsNullOrWhiteSpace(MergeField)) missing.Add("merge_field");
        if (string.IsNullOrWhiteSpace(CsvUrl)) missing.Add("csv_url");
        if (string.IsNullOrWhiteSpace(HeaderRow)) missing.Add("header_row");
        if (missing.Count > 0)
            throw new PipelineNonRetryableException("Validation error: {" + string.Join(", ", missing.Select(m => $"'{m}': 'required field'")) + "}");
    }

    public int MergeFid => ParseFid(MergeField) ?? throw new PipelineNonRetryableException("Validation error: {'merge_field': 'required field'}");

    /// <summary>A field id as the editor saves it: "fid_6" or "6".</summary>
    public static int? ParseFid(string? value)
    {
        var text = value?.Trim() ?? "";
        if (text.StartsWith("fid_", StringComparison.OrdinalIgnoreCase)) text = text[4..];
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fid) && fid > 0 ? fid : null;
    }

    /// <summary>The field each header column goes into, by the column's place (0 = first). Columns left blank are not imported.</summary>
    public IReadOnlyDictionary<int, int> FieldsByColumn(IReadOnlyList<string> header)
    {
        var result = new Dictionary<int, int>();
        var mappings = Mappings;
        var keys = ColumnKeys(header);
        for (var i = 0; i < header.Count; i++)
        {
            if (header[i].Length == 0) continue;
            var mapping = mappings.FirstOrDefault(m => string.Equals(m.Column?.Trim(), keys[i], StringComparison.OrdinalIgnoreCase));
            if (mapping is null || string.IsNullOrWhiteSpace(mapping.Field)) continue;
            if (ParseFid(mapping.Field) is { } fid) result[i] = fid;
        }
        return result;
    }

    /// <summary>The mapping key of each Header row column, as Quickbase keeps them apart: a name used again gets "_0", then "_1" ("Name", "Name_0").</summary>
    public static IReadOnlyList<string> ColumnKeys(IReadOnlyList<string> header)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return header.Select(name =>
        {
            var seen = counts.GetValueOrDefault(name);
            counts[name] = seen + 1;
            return seen == 0 ? name : $"{name}_{seen - 1}";
        }).ToList();
    }

    private static PipelineNonRetryableException Required(string option) => new($"Validation error: {{'{option}': 'required field'}}");

    private static Guid? ParseId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Guid.TryParse(value.Split(':').Last(), out var id) && id != Guid.Empty ? id : null;
}

/// <summary>The counts the step reports: as its output (records_processed / records_added / records_updated / records_unchanged)
/// and at the end of every error message, as Quickbase does ("-- Processed: 0 - Added: 0 - Updated: 0 - Unchanged: 0").</summary>
public sealed record CsvImportCounts(long Processed, long Added, long Updated, long Unchanged)
{
    public static readonly CsvImportCounts None = new(0, 0, 0, 0);

    public string Output() => JsonSerializer.Serialize(new Dictionary<string, long>
    {
        ["records_processed"] = Processed,
        ["records_added"] = Added,
        ["records_updated"] = Updated,
        ["records_unchanged"] = Unchanged
    });

    public string Suffix => $"-- Processed: {Processed} - Added: {Added} - Updated: {Updated} - Unchanged: {Unchanged}";

    public PipelineNonRetryableException Fail(string message) => new($"{message.TrimEnd()}\n{Suffix}");
}

/// <summary>The data rows of the CSV (the first row left out when it holds field names) and how many there are in all; at most
/// <see cref="CsvImportFile.MaxRecords"/> of them are imported.</summary>
public sealed record CsvImportRows(IReadOnlyList<string[]> Rows, int TotalRows, IReadOnlyDictionary<int, CsvImportFile.FieldInfo>? Fields = null);

/// <summary>One import the engine runs for the step: its rows written out as a plain CSV (comma, double quotes, the Header row first) and the import settings.</summary>
public sealed record CsvImportPart(byte[] Csv, int Rows, ImportDefinitionConfig Import);

/// <summary>Reads a CSV the way Quickbase's Import with CSV does (Python csv rules: the chosen separator and quote character, a doubled quote
/// inside quotes is one quote, line breaks inside quotes belong to the value, empty lines are no rows) and rewrites it for the import engine.</summary>
public static class CsvImportFile
{
    /// <summary>Quickbase imports at most this many records in one run; beyond it the first ones are imported and the step fails.</summary>
    public const int MaxRecords = 10_000;

    public static List<string[]> Parse(string text, char delimiter, char quote)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var fieldStarted = false;   // something (even "") was read for the current field
        var rowHasContent = false;  // the current line is not empty
        var i = 0;
        if (text.Length > 0 && text[0] == '﻿') i = 1;

        void EndField()
        {
            row.Add(field.ToString());
            field.Clear();
            fieldStarted = false;
        }
        void EndRow()
        {
            if (rowHasContent)
            {
                EndField();
                rows.Add(row.ToArray());
            }
            row.Clear();
            field.Clear();
            fieldStarted = false;
            rowHasContent = false;
        }

        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == quote)
                {
                    if (i + 1 < text.Length && text[i + 1] == quote) { field.Append(quote); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }
            if (c == quote && !fieldStarted && field.Length == 0)
            {
                inQuotes = true;
                fieldStarted = true;
                rowHasContent = true;
                continue;
            }
            if (c == delimiter)
            {
                rowHasContent = true;
                EndField();
                continue;
            }
            if (c == '\r' || c == '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                EndRow();
                continue;
            }
            rowHasContent = true;
            fieldStarted = true;
            field.Append(c);
        }
        EndRow();
        return rows;
    }

    /// <summary>The data rows: the first row of the file is left out when it holds field names; only the first <see cref="MaxRecords"/> are kept.</summary>
    public static CsvImportRows Prepare(string text, CsvImportDefinition config)
    {
        var parsed = Parse(text, config.Delimiter, config.QuoteChar);
        var data = config.FirstRowIsFieldNames && parsed.Count > 0 ? parsed.Skip(1).ToList() : parsed;
        return new CsvImportRows(data.Take(MaxRecords).ToList(), data.Count);
    }

    /// <summary>A mapped field as the checks before the import need it.</summary>
    public sealed record FieldInfo(string TypeCode, string Label, bool IsUnique, bool IsRecordId = false);

    /// <summary>The field id of the table's Record ID# (the built-in field the table numbers its records with), read from the field list, not assumed.</summary>
    public static int? RecordIdFid(IReadOnlyDictionary<int, FieldInfo>? fields) =>
        fields?.Where(p => p.Value.IsRecordId).Select(p => (int?)p.Key).FirstOrDefault();

    /// <summary>The checks Quickbase makes before it imports anything (nothing is imported when one fails): the merge field must be one of the
    /// mapped columns, and no unique field may hold the same value twice in the CSV.</summary>
    public static void CheckBeforeImport(CsvImportDefinition config, IReadOnlyDictionary<int, int> fieldsByColumn, IReadOnlyList<string[]> rows,
        IReadOnlyDictionary<int, FieldInfo> fields)
    {
        // Quickbase (verified 2026-11-27): with Record ID# as the merge field and no column mapped to it, every row is added as a new record.
        var recordIdFid = RecordIdFid(fields);
        if (config.MergeFid != recordIdFid && !fieldsByColumn.Values.Contains(config.MergeFid))
            throw CsvImportCounts.None.Fail("Invalid input; The merge field is missing from your import data.; Error Code: 2");
        var problems = new List<string>();
        foreach (var (column, fid) in fieldsByColumn.OrderBy(p => p.Key))
        {
            if (fid == recordIdFid) continue;
            var known = fields.TryGetValue(fid, out var field);
            if (!(fid == config.MergeFid || (known && field!.IsUnique))) continue;
            var duplicates = rows.Where(r => column < r.Length && !string.IsNullOrWhiteSpace(r[column]))
                .GroupBy(r => r[column].Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (duplicates.Count > 0)
                problems.Add($"Field name: {(known ? field!.Label : "fid " + fid)}\nDuplicate values:\n\t\t{string.Join("\n\t\t", duplicates)}");
        }
        if (problems.Count > 0)
            throw CsvImportCounts.None.Fail("Quickbase reported an error: Trying to add a non-unique value to a field marked unique; "
                + "Your import data has duplicate values for the following unique fields\n\n" + string.Join("\n", problems) + "; Error Code: 51");
    }

    /// <summary>Quickbase (verified 2026-11-27) leaves out a row whose mapped columns are all empty: it is not imported and not counted as processed.</summary>
    public static IReadOnlyList<string[]> WithMappedValues(IReadOnlyList<string[]> rows, IReadOnlyDictionary<int, int> fieldsByColumn) =>
        fieldsByColumn.Count == 0 ? rows
            : rows.Where(r => fieldsByColumn.Keys.Any(c => c < r.Length && !string.IsNullOrWhiteSpace(r[c]))).ToList();

    private static readonly string[] TrueWords =["true", "yes", "y", "1", "checked", "on"];
    private static readonly string[] DateFormats = ["yyyy-MM-dd", "yyyy/MM/dd", "MM-dd-yyyy", "M-d-yyyy", "MM/dd/yyyy", "M/d/yyyy", "MM-dd-yy", "M/d/yy", "dd-MM-yyyy", "d-M-yyyy", "dd/MM/yyyy", "d/M/yyyy"];

    /// <summary>Values as Quickbase stores them instead of rejecting the row: a number keeps its digits ("$1,234.50" is 1234.5, "1e3" is 1000) and is
    /// left empty when it is not one; a checkbox is checked for true/yes/y/1/checked/on and unchecked for anything else; a date it cannot read is left empty.</summary>
    public static IReadOnlyList<string[]> Clean(IReadOnlyList<string[]> rows, IReadOnlyDictionary<int, int> fieldsByColumn, IReadOnlyDictionary<int, FieldInfo> fields)
    {
        var kinds = fieldsByColumn.Where(p => fields.ContainsKey(p.Value)).Select(p => (Column: p.Key, Type: fields[p.Value].TypeCode)).ToList();
        if (kinds.Count == 0) return rows;
        return rows.Select(row =>
        {
            var copy = (string[])row.Clone();
            foreach (var (column, type) in kinds)
                if (column < copy.Length && !string.IsNullOrWhiteSpace(copy[column])) copy[column] = CleanValue(copy[column].Trim(), type);
            return copy;
        }).ToList();
    }

    private static DateTime? PastMonthEnd(string value)
    {
        var m = System.Text.RegularExpressions.Regex.Match(value, @"^(\d{4})[-/](\d{1,2})[-/](\d{1,2})$");
        int year, month, day;
        if (m.Success) (year, month, day) = (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
        else if ((m = System.Text.RegularExpressions.Regex.Match(value, @"^(\d{1,2})[-/](\d{1,2})[-/](\d{4})$")).Success)
            (month, day, year) = (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
        else return null;
        if (year < 1 || month is < 1 or > 12 || day is < 1 or > 31) return null;
        return new DateTime(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));
    }

    public static string CleanValue(string value, string typeCode)
    {
        switch (typeCode)
        {
            case "Number" or "Currency" or "Percent" or "Rating":
                var digits = new string(value.Where(c => c is not ('$' or ',' or '%' or ' ')).ToArray());
                return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
                    ? number.ToString("R", CultureInfo.InvariantCulture) : "";
            case "Boolean":
                return TrueWords.Contains(value, StringComparer.OrdinalIgnoreCase) ? "true" : "false";
            case "Date":
                if (DateTime.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    || DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                    return date.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);
                // Quickbase reads digits alone as milliseconds since 1970 ("20250131" is 01-01-1970) and moves a day past the
                // month's end back to its last day ("2025-02-30" is 02-28-2025).
                if (value.All(char.IsAsciiDigit) && long.TryParse(value, out var ms))
                    return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);
                if (PastMonthEnd(value) is { } clamped) return clamped.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);
                return "";
            default:
                return value;
        }
    }

    /// <summary>The imports that bring the rows in, in order. When a column goes into the merge field, rows with a value there are matched on it (update
    /// a matching record, add one otherwise) and rows where it is blank are added, as Quickbase does; with no column in the merge field every row is added.</summary>
    public static IReadOnlyList<CsvImportPart> BuildParts(CsvImportDefinition config, IReadOnlyList<string> header, IReadOnlyDictionary<int, int> fieldsByColumn,
        IReadOnlyList<string[]> rows, IReadOnlyDictionary<int, FieldInfo>? fields = null)
    {
        var mergeFid = config.MergeFid;
        var recordIdFid = RecordIdFid(fields);
        var mappings = new List<(int Column, int Fid)>();
        foreach (var (column, fid) in fieldsByColumn.OrderBy(p => p.Key))
            if (mappings.All(m => m.Fid != fid)) mappings.Add((column, fid)); // a field takes the first column mapped into it
        var mergeColumn = mappings.Where(m => m.Fid == mergeFid).Select(m => (int?)m.Column).FirstOrDefault();
        if (mergeColumn is null) return rows.Count == 0 ? [] : [Part(header, mappings, rows, null)];

        static bool Blank(string[] row, int column) => column >= row.Length || string.IsNullOrWhiteSpace(row[column]);
        var keyed = rows.Where(r => !Blank(r, mergeColumn.Value)).ToList();
        var unkeyed = rows.Where(r => Blank(r, mergeColumn.Value)).ToList();
        var parts = new List<CsvImportPart>();
        if (keyed.Count > 0) parts.Add(Part(header, mappings, keyed, mergeFid));
        // The Record ID# is never written: rows without one are added with the other columns.
        if (unkeyed.Count > 0) parts.Add(Part(header, mergeFid == recordIdFid ? mappings.Where(m => m.Fid != recordIdFid).ToList() : mappings, unkeyed, null));
        return parts.Where(p => p.Import.Mappings.Count > 0).ToList();
    }

    /// <summary>Dates as Quickbase reads them by default (month first, or year-month-day); times on a 24-hour clock unless AM/PM is written.</summary>
    public const string DateFormat = "MM-DD-YYYY";
    public const string TimeFormat = "HH:mm:ss";

    private static CsvImportPart Part(IReadOnlyList<string> header, IReadOnlyList<(int Column, int Fid)> mappings, IReadOnlyList<string[]> rows, int? mergeFid)
    {
        var builder = new StringBuilder();
        builder.Append(string.Join(",", header.Select((name, i) => Quote(ColumnName(name, i))))).Append("\r\n");
        foreach (var values in rows)
            builder.Append(string.Join(",", Enumerable.Range(0, header.Count).Select(c => Quote(c < values.Length ? values[c] : "")))).Append("\r\n");
        var import = new ImportDefinitionConfig
        {
            Name = "Import with CSV",
            SourceKind = ImportSourceKinds.File,
            File = new ImportFileOptions
            {
                Format = ImportFileFormats.Csv,
                HeaderRow = 1,
                Delimiter = ",",
                ColumnBy = ImportSourceKinds.ColumnByPosition,
                Columns = header.Select(ColumnName).ToList()
            },
            ImportType = mergeFid is null ? ImportTypes.Copy : ImportTypes.Merge,
            MergeKeyFid = mergeFid,
            Mappings = mappings.Select(m => new ImportFieldMapping
            {
                DestFid = m.Fid, Source = ImportMappingSource.Dynamic, SourceFid = ImportFileColumns.FidOf(m.Column + 1), DateFormat = DateFormat, TimeFormat = TimeFormat
            }).ToList(),
            ConstraintPolicy = ImportConstraintPolicy.ImportValid
        };
        return new CsvImportPart(new UTF8Encoding(false).GetBytes(builder.ToString()), rows.Count, import);
    }

    private static string ColumnName(string name, int index) => name.Length == 0 ? $"Column {index + 1}" : name;

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
