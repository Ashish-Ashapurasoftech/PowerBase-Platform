using System.Globalization;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Imports.Files;

/// <param name="Name">The sheet (a workbook); null for a CSV file.</param>
public sealed record ImportSheetRequest(string? Name, int HeaderRow = 1, int? DataStartRow = null, string? Delimiter = null);

/// <param name="TableId">The destination table whose fields the columns are matched against.</param>
public sealed record ImportColumnFitRequest(Guid TableId, List<ImportSheetRequest>? Sheets);

/// <summary>One column of a sheet, as seen from one destination table.</summary>
/// <param name="FieldFid">The field of the table the heading matches, when it matches one (even if the data then does not fit it).</param>
/// <param name="Fits">The column is offered for this table: it matches a field and every filled cell in the sample fits that field.</param>
/// <param name="Reason">"none" when it fits; "noField" when no field of the table has this name; "type" when the data does not fit the field.</param>
/// <param name="Sampled">Filled cells looked at.</param>
/// <param name="Failed">Of those, how many do not fit the field.</param>
/// <param name="Example">The first value that does not fit (shortened), to say why.</param>
/// <param name="FitsFields">Every field of the table this column's data fits, whatever its heading is: where it can be mapped by hand.</param>
public sealed record ImportColumnFitColumn(
    int Fid, string Name, int? FieldFid, string? FieldName, bool Fits, string Reason, int Sampled, int Failed, string? Example, IReadOnlyList<int>? FitsFields = null);

/// <param name="Error">Why the sheet could not be read (its row of column names is missing), instead of columns.</param>
public sealed record ImportSheetFit(string? Sheet, IReadOnlyList<ImportColumnFitColumn> Columns, string? Error = null);

public sealed record ImportColumnFitResponse(IReadOnlyList<ImportSheetFit> Sheets);

/// <summary>Decides, for one destination table, which columns of a file's sheets it can be given: only those whose heading is the name of one
/// of the table's fields (ignoring case, spaces, underscores and punctuation) and whose data fits that field's type. A file has no types of its
/// own, so the first rows of each sheet are tried with the very conversion and format checks a run applies; a column is offered only when every
/// filled cell passes, so the screen never offers a pairing the run would reject row after row. Reads, never writes.</summary>
public static class ImportColumnFit
{
    /// <summary>Rows of a sheet looked at.</summary>
    public const int SampleRows = 200;

    /// <summary>The importable fields of the table: not the Record ID#, system, formula or unsupported types.</summary>
    public static IReadOnlyList<AppField> Candidates(IEnumerable<AppField> fields) =>
        fields.Where(f => ImportTypeCompatibility.IsWritable(f) && !ImportTypeCompatibility.IsRecordId(f)).ToList();

    /// <summary>A heading or field name with case, spaces, underscores and punctuation removed: "Employee ID" and "employee_id" are the same.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var chars = text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray();
        return new string(chars);
    }

    /// <summary>The field a heading names, or null. Several fields that read alike: the one spelled exactly like the heading, then the one
    /// that differs only in case, then the first.</summary>
    public static AppField? FieldFor(string heading, IReadOnlyList<AppField> candidates)
    {
        var key = Normalize(heading);
        if (key.Length == 0) return null;
        var alike = candidates.Where(f => Normalize(ImportTypeCompatibility.DisplayName(f)) == key || Normalize(f.Name) == key).ToList();
        if (alike.Count <= 1) return alike.FirstOrDefault();
        var trimmed = heading.Trim();
        return alike.FirstOrDefault(f => ImportTypeCompatibility.DisplayName(f) == trimmed || f.Name == trimmed)
            ?? alike.FirstOrDefault(f => string.Equals(ImportTypeCompatibility.DisplayName(f), trimmed, StringComparison.OrdinalIgnoreCase))
            ?? alike[0];
    }

    /// <summary>Tries every filled sample value against the field the way a run would convert and check it.</summary>
    public static (int Sampled, int Failed, string? Example) Check(AppField field, IEnumerable<string?> sample)
    {
        int sampled = 0, failed = 0;
        string? example = null;
        var format = ImportFieldFormat.For(field);
        foreach (var raw in sample)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            sampled++;
            if (ImportTypeCompatibility.TryConvert(raw, field, out var converted, out _) && format?.Check(converted) is null) continue;
            failed++;
            example ??= raw.Length <= 40 ? raw : raw[..40] + "...";
        }
        return (sampled, failed, example);
    }

    /// <summary>True when at least one filled sample value converts to the field, or nothing is filled (no evidence against it).</summary>
    private static bool HoldsSome(AppField field, IReadOnlyList<string?> sample)
    {
        var (sampled, failed, _) = Check(field, sample);
        return sampled == 0 || failed < sampled;
    }

    /// <param name="byHandOnly">Fields a column can be chosen for by hand but never matches by its heading (the Record ID#, which an import can map only when it
    /// matches records on it): they are added to the fields a column is offered when at least one of its values converts (the run reports the others row by row).</param>
    public static IReadOnlyList<ImportColumnFitColumn> Evaluate(
        IReadOnlyList<ImportFileColumn> columns, IReadOnlyList<string?[]> rows, IReadOnlyList<AppField> candidates, IReadOnlyList<AppField>? byHandOnly = null)
    {
        var result = new List<ImportColumnFitColumn>(columns.Count);
        foreach (var column in columns)
        {
            var at = column.Position - 1;
            var sample = rows.Select(r => at < r.Length ? r[at] : null).ToList();
            var fitting = FieldsFitting(sample, candidates);
            if (byHandOnly is { Count: > 0 })
            {
                // Such a field takes a column that holds some values for it: the rows that do not convert are reported by the run, one by one, with the reason.
                var partial = byHandOnly.Where(f => f.Fid is not null && HoldsSome(f, sample)).Select(f => f.Fid!.Value);
                fitting = fitting.Concat(partial).ToList();
            }
            var field = FieldFor(column.Name, candidates);
            if (field is null)
            {
                result.Add(new ImportColumnFitColumn(column.Fid, column.UniqueName, null, null, false, "noField", 0, 0, null, fitting));
                continue;
            }
            var (sampled, failed, example) = Check(field, sample);
            var fits = failed == 0;
            result.Add(new ImportColumnFitColumn(column.Fid, column.UniqueName, field.Fid, ImportTypeCompatibility.DisplayName(field), fits, fits ? "none" : "type", sampled, failed, example, fitting));
        }
        return result;
    }

    /// <summary>The fields a column's data fits, wherever the heading says it goes: every filled cell of the sample converts to the field and passes its format.
    /// Fields of one type with no format share an answer, so a table with hundreds of fields costs a few checks per column.</summary>
    public static IReadOnlyList<int> FieldsFitting(IReadOnlyList<string?> sample, IReadOnlyList<AppField> candidates)
    {
        var byKind = new Dictionary<string, bool>();
        var fitting = new List<int>();
        foreach (var field in candidates)
        {
            if (field.Fid is not int fid) continue;
            var key = ImportFieldFormat.For(field) is null ? $"{field.TypeCode}|{field.MaxLength}" : $"#{field.Id}";
            if (!byKind.TryGetValue(key, out var fits)) byKind[key] = fits = Check(field, sample).Failed == 0;
            if (fits) fitting.Add(fid);
        }
        return fitting;
    }

    /// <summary>The columns of a sheet and its first filled data rows, read once and streamed.</summary>
    public static (IReadOnlyList<ImportFileColumn> Columns, IReadOnlyList<string?[]> Rows) Sample(Stream stream, ImportFileLayout layout)
    {
        var delimiter = layout.Format == ImportFileFormats.Csv ? layout.Delimiter ?? ImportCsvReader.DetectDelimiter(stream) : (char?)null;
        layout = layout with { Delimiter = delimiter };
        string?[]? header = null;
        var width = 0;
        var rows = new List<string?[]>(SampleRows);
        using var reader = ImportFileInspector.Open(stream, layout);
        while (reader.Read(out var rowNumber, out var cells))
        {
            if (layout.HeaderRow > 0 && rowNumber == layout.HeaderRow) { header = cells; width = Math.Max(width, cells.Length); }
            if (rowNumber < layout.FirstDataRow || ImportFileInspector.IsBlank(cells)) continue;
            width = Math.Max(width, cells.Length);
            if (rows.Count < SampleRows) rows.Add(cells);
            else break;
        }
        if (layout.HeaderRow > 0 && header is null) throw new ImportFileHeaderMissingException($"Row {layout.HeaderRow}, which should hold the column names, is not in the file.");
        return (ImportFileColumns.FromHeader(header, width), rows);
    }
}

/// <summary>For one destination table: which columns of each sheet of an uploaded file it is offered. One call for all the sheets, so the
/// screen asks once per table. Only the person who uploaded the file may ask, and only about a table they may add records to.</summary>
public sealed class ColumnFitHandler(
    IImportFileRepository files, IImportFileAccess fileAccess, IAppTableRepository tables, IAppFieldRepository fields, IAppAccessService access, IQueryContext user)
{
    public const int MaxSheets = 100;

    public async Task<ImportColumnFitResponse> HandleAsync(Guid fileId, ImportColumnFitRequest request, CancellationToken ct)
    {
        var file = await ImportFileOwnership.LoadAsync(fileId, files, user, ct);
        await access.RequirePermissionByTablePublicIdAsync(request.TableId, PermissionCodes.RecordsCreate, ct);
        var sheets = request.Sheets is { Count: > 0 } ? request.Sheets : [new ImportSheetRequest(null)];
        if (sheets.Count > MaxSheets) throw ImportFileErrors.Invalid($"At most {MaxSheets} sheets can be checked at once.");

        var table = await tables.GetByPublicIdAsync(request.TableId, ct);
        var tableFields = await fields.ListByTableAsync(table.Id, ct);
        var candidates = ImportColumnFit.Candidates(tableFields);
        var recordId = tableFields.Where(f => !f.IsDeleted && ImportTypeCompatibility.IsRecordId(f)).ToList();

        await using var stream = await fileAccess.OpenAsync(file.StoragePath, ct);
        IReadOnlyList<string> names = file.Format == ImportFileFormats.Xlsx ? ImportXlsxReader.SheetNames(stream) : [];
        var result = new List<ImportSheetFit>(sheets.Count);
        foreach (var sheet in sheets)
        {
            if (file.Format == ImportFileFormats.Xlsx && !names.Contains(sheet.Name ?? "", StringComparer.OrdinalIgnoreCase))
                throw ImportFileErrors.Invalid($"The workbook has no sheet called '{sheet.Name}'.");
            var options = ImportFileValidation.ValidateLayout(new ImportFileOptions
            {
                Format = file.Format, Sheet = file.Format == ImportFileFormats.Xlsx ? sheet.Name : null, HeaderRow = sheet.HeaderRow,
                DataStartRow = sheet.DataStartRow, Delimiter = sheet.Delimiter
            });
            stream.Position = 0;
            try
            {
                var (columns, rows) = ImportColumnFit.Sample(stream, options.ToLayout());
                result.Add(new ImportSheetFit(sheet.Name, ImportColumnFit.Evaluate(columns, rows, candidates, recordId)));
            }
            catch (ImportFileHeaderMissingException ex) { result.Add(new ImportSheetFit(sheet.Name, [], ex.Message)); }
            catch (ImportFileFormatException ex) { throw ImportFileErrors.Invalid(ex.Message); }
        }
        return new ImportColumnFitResponse(result);
    }
}
