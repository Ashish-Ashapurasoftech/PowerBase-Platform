using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Imports.Files;

/// <summary>Something an import run reads its rows from, a bounded chunk at a time, in the order the source keeps them. The run's cursor
/// is the last row number (a table's Record ID#, a file's row) the reader has examined.</summary>
public interface IImportChunkReader : IAsyncDisposable
{
    Task<ImportSourceReader.Chunk> ReadChunkAsync(ImportPlan plan, long afterId, long maxId, CancellationToken ct);
}

/// <summary>The uploaded file a run reads, as remembered in the run's snapshot. The storage path is internal and never sent to a client.</summary>
public sealed record ImportRunFile(Guid FileId, string StoragePath, string FileName);

/// <summary>What a run needs from the uploaded file: its columns as they are right now, a reader over its rows, and clean-up afterwards.</summary>
public interface IImportFileAccess
{
    /// <summary>The file's columns as they are now. With <paramref name="countRows"/> the whole file is read once to count its rows (a run
    /// needs that for its progress); without it only the top of the file is read, enough to plan and check the import quickly.</summary>
    Task<ImportFileSource> OpenSourceAsync(ImportRunSnapshot snapshot, bool countRows, CancellationToken ct);
    Task<IImportChunkReader> OpenChunkReaderAsync(ImportPlan plan, CancellationToken ct);
    /// <summary>The stored file as a stream that can seek (a workbook is a zip and needs it), however the storage provider hands it back.</summary>
    Task<Stream> OpenAsync(string storagePath, CancellationToken ct);
    /// <summary>Deletes the uploaded file once the run has ended; the data it held must not outlive the import.</summary>
    Task DiscardAsync(ImportRunSnapshot snapshot, CancellationToken ct);
    /// <summary>Keeps the uploaded file a run used until <paramref name="until"/> (the history shows it; it is deleted then), instead of deleting it.</summary>
    Task RetainAsync(ImportRunSnapshot snapshot, DateTime until, CancellationToken ct);
}

/// <summary>Reads a file's rows in the shape the import engine reads a table's: a dictionary per row keyed by column, the row's
/// number standing in for the Record ID#. Rows before the data starts and blank rows are passed over; the rest that satisfy the
/// import's conditions are returned. When every sheet is read they come one sheet after another, each sheet's cells put in the
/// columns the import was planned with. The file is read once, in order, so each reader serves exactly one pass.</summary>
public sealed class ImportFileChunkReader : IImportChunkReader
{
    private readonly Stream _stream;
    private readonly ImportFileSource _file;
    private readonly Dictionary<int, string> _columnNames;
    private readonly bool _byName;
    private IImportRowReader? _reader;
    private int _sheetIndex = -1;
    private int[] _map;
    private long _idBase;
    private long _lastId;
    private bool _ended;

    public ImportFileChunkReader(Stream stream, ImportFileSource file)
    {
        _stream = stream;
        _file = file;
        _byName = file.Options.ColumnBy != ImportSourceKinds.ColumnByPosition;
        _columnNames = file.Columns.ToDictionary(c => c.Position, c => PhysicalNaming.ColumnName(c.Fid));
        _map = Enumerable.Range(0, file.Columns.Count).ToArray();
        if (file.Sheets is null) _reader = ImportFileInspector.Open(stream, file.Layout);
        else NextSheet();
    }

    private void NextSheet()
    {
        _reader?.Dispose();
        _reader = null;
        var sheets = _file.Sheets!;
        if (++_sheetIndex >= sheets.Count) { _ended = true; return; }
        var sheet = sheets[_sheetIndex];
        _idBase = ImportFileSheets.RowId(_sheetIndex, 0);
        _map = ImportFileSheets.Translate(_file.Columns, sheet.Columns, _byName);
        _stream.Position = 0;
        _reader = ImportFileInspector.Open(_stream, _file.Layout with { Sheet = sheet.Name });
    }

    public Task<ImportSourceReader.Chunk> ReadChunkAsync(ImportPlan plan, long afterId, long maxId, CancellationToken ct)
    {
        if (afterId != _lastId && afterId != 0)
            throw new InvalidOperationException("A file can only be read forward, once.");
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var examined = 0;
        while (rows.Count < ImportSourceReader.ChunkSize && !_ended)
        {
            if (_reader is null || !_reader.Read(out var number, out var cells))
            {
                if (_file.Sheets is null) _ended = true; else NextSheet();
                continue;
            }
            _lastId = _idBase + number;
            if ((++examined & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
            if (number < _file.Layout.FirstDataRow || ImportFileInspector.IsBlank(cells)) continue;

            var row = new Dictionary<string, object?>(_map.Length + 1) { ["Id"] = _lastId };
            for (var position = 1; position <= _map.Length; position++)
            {
                var at = _map[position - 1];
                row[_columnNames[position]] = at >= 0 && at < cells.Length ? cells[at] : null;
            }
            if (plan.RowFilter is { } filter && !filter.Matches(row)) continue;
            rows.Add(row);
        }
        return Task.FromResult(new ImportSourceReader.Chunk(rows, _lastId, rows.Count == 0 && _ended));
    }

    public ValueTask DisposeAsync()
    {
        _reader?.Dispose();
        _stream.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class ImportFileAccess(IFileStorageService storage, IImportFileRepository files) : IImportFileAccess
{
    public async Task<ImportFileSource> OpenSourceAsync(ImportRunSnapshot snapshot, bool countRows, CancellationToken ct)
    {
        var run = snapshot.File ?? throw new InvalidOperationException("The run has no file.");
        var options = snapshot.Config.File ?? throw new InvalidOperationException("The import has no file settings.");
        await using var stream = await OpenAsync(run.StoragePath, ct);
        var layout = options.ToLayout();
        if (layout.Format == ImportFileFormats.Csv && layout.Delimiter is null)
        {
            // Settle the separator once, so every pass of the run splits the rows the same way.
            layout = layout with { Delimiter = ImportCsvReader.DetectDelimiter(stream) };
            stream.Position = 0;
        }
        if (options.AllSheets) return await OpenAllSheetsAsync(stream, run, layout, options, countRows);
        if (!countRows) return new ImportFileSource(ImportFileInspector.ReadColumns(stream, layout), run.FileName, layout, run.StoragePath, 0, 0, options);
        var preview = ImportFileInspector.Preview(stream, layout);
        return new ImportFileSource(preview.Columns, run.FileName, layout, run.StoragePath, preview.DataRows, preview.LastRow, options);
    }

    /// <summary>Reads each sheet's column names (and, for a run, counts its rows). A sheet with no column-name row at all (an empty sheet)
    /// is passed over; the first sheet that has one sets the columns the import is planned against.</summary>
    private static Task<ImportFileSource> OpenAllSheetsAsync(Stream stream, ImportRunFile run, ImportFileLayout layout, ImportFileOptions options, bool countRows)
    {
        if (layout.Format != ImportFileFormats.Xlsx) throw new ImportFileFormatException("Reading every sheet needs an Excel workbook.");
        var names = ImportXlsxReader.SheetNames(stream);
        var sheets = new List<ImportFileSheet>();
        foreach (var name in names)
        {
            stream.Position = 0;
            var sheetLayout = layout with { Sheet = name };
            try
            {
                if (countRows)
                {
                    var preview = ImportFileInspector.Preview(stream, sheetLayout);
                    sheets.Add(new ImportFileSheet(name, preview.Columns, preview.DataRows, preview.LastRow));
                }
                else sheets.Add(new ImportFileSheet(name, ImportFileInspector.ReadColumns(stream, sheetLayout), 0, 0));
            }
            catch (ImportFileHeaderMissingException) { /* nothing on this sheet to import */ }
        }
        if (sheets.Count == 0) throw new ImportFileFormatException("None of the sheets has a row of column names where the import expects it.");
        var last = sheets.FindLastIndex(s => s.DataRows > 0);
        return Task.FromResult(new ImportFileSource(
            sheets[0].Columns, run.FileName, layout, run.StoragePath, sheets.Sum(s => s.DataRows),
            last < 0 ? 0 : ImportFileSheets.RowId(last, sheets[last].LastRow), options, sheets));
    }

    public async Task<IImportChunkReader> OpenChunkReaderAsync(ImportPlan plan, CancellationToken ct)
    {
        var file = plan.File ?? throw new InvalidOperationException("The plan has no file.");
        var stream = await OpenAsync(file.StoragePath, ct);
        try { return new ImportFileChunkReader(stream, file); }
        catch { await stream.DisposeAsync(); throw; }
    }

    public async Task DiscardAsync(ImportRunSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.File is not { } file) return;
        try { await storage.DeleteAsync(file.StoragePath, ct); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the nightly clean-up retries from the record below */ }
        await files.DeleteAsync(file.FileId, ct);
    }

    public async Task RetainAsync(ImportRunSnapshot snapshot, DateTime until, CancellationToken ct)
    {
        if (snapshot.File is not { } file) return;
        await files.RetainAsync(file.FileId, until, ct);
    }

    public async Task<Stream> OpenAsync(string path, CancellationToken ct)
    {
        if (storage is not IFileStorageReadService readable) throw new InvalidOperationException("The configured file storage cannot read files back.");
        var stream = await readable.OpenReadAsync(path, ct);
        if (stream.CanSeek) return stream;
        var temp = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
        await using (stream) await stream.CopyToAsync(temp, ct);
        temp.Position = 0;
        return temp;
    }
}
