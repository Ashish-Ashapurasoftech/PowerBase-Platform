using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Imports.Files;

public sealed record ImportFileInfo(Guid FileId, string FileName, string Format, long SizeBytes, IReadOnlyList<string> Sheets);

/// <summary>One sheet of a workbook read as a whole: its rows, and the columns of the first sheet it does not have (found by heading).</summary>
public sealed record ImportFileSheetSummary(string Name, long DataRows, IReadOnlyList<string> MissingColumns, bool Skipped = false);

public sealed record ImportFilePreviewResponse(
    IReadOnlyList<ImportFileColumn> Columns, IReadOnlyList<string?[]> Rows, long DataRows, long LastRow, string? Delimiter, IReadOnlyList<string> Sheets,
    IReadOnlyList<ImportFileSheetSummary>? SheetSummaries = null);

internal static class ImportFileErrors
{
    public static ValidationException Invalid(string message) => new(new Dictionary<string, string[]> { ["File"] = [message] });
}

/// <summary>Checks the settings a file import is saved with, and fills in the defaults.</summary>
public static class ImportFileValidation
{
    public static ImportFileOptions Validate(ImportFileOptions? input)
    {
        var layout = ValidateLayout(input);
        var columns = (input!.Columns ?? []).Select(c => (c ?? "").Trim()).ToList();
        if (columns.Count is < 1 or > ImportFileFormats.MaxColumns) throw ImportFileErrors.Invalid($"The file must have between 1 and {ImportFileFormats.MaxColumns} columns.");
        if (columns.Any(c => c.Length > 500)) throw ImportFileErrors.Invalid("A column name is too long.");
        layout.Columns = columns;
        return layout;
    }

    /// <summary>The settings that say where the table is in the file (type, sheet, rows, separator, how columns are found), cleaned up.</summary>
    public static ImportFileOptions ValidateLayout(ImportFileOptions? input)
    {
        if (input is null) throw ImportFileErrors.Invalid("Choose how the file is read.");
        var format = input.Format?.Trim().ToLowerInvariant();
        if (format is not (ImportFileFormats.Csv or ImportFileFormats.Xlsx)) throw ImportFileErrors.Invalid("The file type must be CSV or Excel (.xlsx).");
        if (input.HeaderRow is < 0 or > ImportFileLayout.MaxHeaderRow) throw ImportFileErrors.Invalid($"The row with the column names must be between 0 and {ImportFileLayout.MaxHeaderRow}.");
        if (input.DataStartRow is { } start && (start < 1 || start <= input.HeaderRow || start > 1_000_000))
            throw ImportFileErrors.Invalid("The data must start below the row with the column names.");
        if (input.ColumnBy is not (ImportSourceKinds.ColumnByName or ImportSourceKinds.ColumnByPosition)) throw ImportFileErrors.Invalid("Choose whether columns are found by name or by position.");
        var delimiter = string.IsNullOrEmpty(input.Delimiter) ? null : input.Delimiter;
        if (delimiter is not null && delimiter != "\\t" && (delimiter.Length != 1 || delimiter[0] is '"' or '\n' or '\r'))
            throw ImportFileErrors.Invalid("The separator must be a single character.");
        var sheet = input.Sheet?.Trim();
        if (sheet is { Length: > 100 }) throw ImportFileErrors.Invalid("The sheet name is too long.");
        if (input.AllSheets && format != ImportFileFormats.Xlsx) throw ImportFileErrors.Invalid("Reading every sheet needs an Excel workbook.");
        return new ImportFileOptions
        {
            Format = format, Sheet = input.AllSheets || string.IsNullOrWhiteSpace(sheet) ? null : sheet, HeaderRow = input.HeaderRow, DataStartRow = input.DataStartRow,
            Delimiter = delimiter, ColumnBy = input.ColumnBy, AllSheets = input.AllSheets
        };
    }

    /// <summary>The columns an import was saved with, standing in for a file while an import is saved or checked (no file is at hand then).</summary>
    public static ImportFileSource SavedSource(ImportFileOptions options) => new(
        ImportFileColumns.FromHeader(options.Columns.ToArray(), options.Columns.Count), "the file", options.ToLayout(), "", 0, 0, options);
}

/// <summary>Takes in an upload: checks its size and what is really inside it, keeps it under a name nobody can guess, and says which
/// sheets it has. The person then chooses how to read it. Nothing is imported yet.</summary>
public sealed class UploadImportFileHandler(IAppAccessService access, IFileStorageService storage, IImportFileRepository files, IQueryContext user)
{
    public const int MaxOutstandingPerUser = 10;

    public async Task<ImportFileInfo> HandleAsync(Guid destinationTableId, Stream content, string fileName, long length, CancellationToken ct)
    {
        await access.RequirePermissionByTablePublicIdAsync(destinationTableId, PermissionCodes.RecordsCreate, ct);
        if (length <= 0) throw ImportFileErrors.Invalid("The file is empty.");
        if (length > ImportFileFormats.MaxFileBytes) throw ImportFileErrors.Invalid($"The file is larger than {ImportFileFormats.MaxFileBytes / (1024 * 1024)} MB. Split it into smaller files.");
        if (await files.CountByUserAsync(user.UserId, ct) >= MaxOutstandingPerUser)
            throw new ConflictException("You have several uploaded files waiting. Import or discard them before uploading more.");

        var head = new byte[8];
        var read = await ReadHeadAsync(content, head, ct);
        content.Position = 0;
        var safeName = SafeName(fileName);
        var format = ImportFileFormats.Detect(safeName, head.AsSpan(0, read))
            ?? throw ImportFileErrors.Invalid("This file type cannot be imported. Use an Excel workbook (.xlsx) or a CSV file. For an older .xls file, save it as .xlsx first.");

        var stored = await storage.SaveAsync(content, safeName, null, ct, uniqueKey: $"import-{Guid.NewGuid():N}");
        try
        {
            IReadOnlyList<string> sheets = [];
            if (format == ImportFileFormats.Xlsx)
            {
                content.Position = 0;
                sheets = ImportXlsxReader.SheetNames(content);
                if (sheets.Count == 0) throw new ImportFileFormatException("The workbook has no sheets.");
            }
            var record = new ImportFile { UploadedByUserId = user.UserId, FileName = safeName, StoragePath = stored.Path, Format = format, SizeBytes = length };
            await files.CreateAsync(record, ct);
            return new ImportFileInfo(record.PublicId, safeName, format, length, sheets);
        }
        catch (ImportFileFormatException ex)
        {
            await storage.DeleteAsync(stored.Path, CancellationToken.None);
            throw ImportFileErrors.Invalid(ex.Message);
        }
        catch
        {
            await storage.DeleteAsync(stored.Path, CancellationToken.None);
            throw;
        }
    }

    private static async Task<int> ReadHeadAsync(Stream content, byte[] head, CancellationToken ct)
    {
        var total = 0;
        while (total < head.Length)
        {
            var n = await content.ReadAsync(head.AsMemory(total), ct);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>The file's own name, without any folder part, control characters or excess length. Only ever shown back and used for the
    /// extension: the stored name is generated.</summary>
    public static string SafeName(string fileName)
    {
        var name = Path.GetFileName(fileName ?? "") ?? "";
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length > 200) name = name[^200..];
        return name.Length == 0 ? "upload" : name;
    }
}

/// <summary>Shows the top of an uploaded file as a table, read the way the person has chosen (sheet, header row, separator), so they can
/// see the columns and map them before running anything. Reads the whole file once to count its rows, streaming.</summary>
public sealed class PreviewImportFileHandler(IImportFileRepository files, IImportFileAccess fileAccess, IQueryContext user)
{
    public async Task<ImportFilePreviewResponse> HandleAsync(
        Guid fileId, string? sheet, int? headerRow, int? dataStartRow, string? delimiter, CancellationToken ct, bool allSheets = false)
    {
        var file = await ImportFileOwnership.LoadAsync(fileId, files, user, ct);
        var options = ImportFileValidation.ValidateLayout(
            new ImportFileOptions { Format = file.Format, Sheet = sheet, HeaderRow = headerRow ?? 1, DataStartRow = dataStartRow, Delimiter = delimiter, AllSheets = allSheets });
        await using var stream = await fileAccess.OpenAsync(file.StoragePath, ct);
        try
        {
            IReadOnlyList<string> sheets = file.Format == ImportFileFormats.Xlsx ? ImportXlsxReader.SheetNames(stream) : [];
            stream.Position = 0;
            var layout = options.ToLayout();
            if (options.AllSheets) return AllSheetsPreview(stream, layout, sheets);
            // A workbook is read from its first sheet unless the person chose one.
            if (file.Format == ImportFileFormats.Xlsx && layout.Sheet is null) layout = layout with { Sheet = sheets.FirstOrDefault() };
            var preview = ImportFileInspector.Preview(stream, layout);
            return new ImportFilePreviewResponse(preview.Columns, preview.Rows, preview.DataRows, preview.LastRow, preview.Delimiter, sheets);
        }
        catch (ImportFileFormatException ex) { throw ImportFileErrors.Invalid(ex.Message); }
    }

    /// <summary>The first sheet's columns and rows stand for the import (every sheet is read with them); the total counts every sheet, and
    /// each sheet is listed with the columns it lacks, so a sheet laid out differently is seen before the import is run.</summary>
    private static ImportFilePreviewResponse AllSheetsPreview(Stream stream, ImportFileLayout layout, IReadOnlyList<string> sheets)
    {
        ImportFilePreview? first = null;
        long total = 0;
        var summaries = new List<ImportFileSheetSummary>();
        foreach (var name in sheets)
        {
            stream.Position = 0;
            try
            {
                var preview = ImportFileInspector.Preview(stream, layout with { Sheet = name });
                first ??= preview;
                total += preview.DataRows;
                var missing = first.Columns.Where(c => !preview.Columns.Any(o => string.Equals(o.Name, c.Name, StringComparison.OrdinalIgnoreCase))).Select(c => c.Name).ToList();
                summaries.Add(new ImportFileSheetSummary(name, preview.DataRows, missing));
            }
            catch (ImportFileHeaderMissingException) { summaries.Add(new ImportFileSheetSummary(name, 0, [], Skipped: true)); }
        }
        if (first is null) throw ImportFileErrors.Invalid("None of the sheets has a row of column names where you said it is.");
        return new ImportFilePreviewResponse(first.Columns, first.Rows, total, 0, null, sheets, summaries);
    }
}

public sealed class DiscardImportFileHandler(IImportFileRepository files, IFileStorageService storage, IQueryContext user)
{
    public async Task HandleAsync(Guid fileId, CancellationToken ct)
    {
        var file = await ImportFileOwnership.LoadAsync(fileId, files, user, ct);
        await storage.DeleteAsync(file.StoragePath, ct);
        await files.DeleteAsync(file.PublicId, ct);
    }
}

internal static class ImportFileOwnership
{
    /// <summary>An uploaded file belongs to the person who uploaded it; to everyone else it does not exist.</summary>
    public static async Task<ImportFile> LoadAsync(Guid fileId, IImportFileRepository files, IQueryContext user, CancellationToken ct)
    {
        var file = await files.GetByPublicIdAsync(fileId, ct);
        if (file is null || file.UploadedByUserId != user.UserId) throw new NotFoundException("ImportFile", fileId);
        return file;
    }
}

/// <summary>Removes uploads nobody imported: a file is only ever kept for a day.</summary>
public sealed class ImportFileCleanup(IImportFileRepository files, IFileStorageService storage)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    private const int Batch = 50;

    public async Task<int> RunAsync(DateTime nowUtc, CancellationToken ct)
    {
        var old = await files.ListOlderThanAsync(nowUtc - MaxAge, Batch, ct);
        foreach (var file in old)
        {
            try { await storage.DeleteAsync(file.StoragePath, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; } // try again next time
            await files.DeleteAsync(file.PublicId, ct);
        }
        return old.Count;
    }
}
