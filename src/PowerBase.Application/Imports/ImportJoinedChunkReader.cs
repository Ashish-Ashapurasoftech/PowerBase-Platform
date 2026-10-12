using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports.Files;
using PowerBase.Formula;

namespace PowerBase.Application.Imports;

/// <summary>Reads the sheets a table takes rows from as one source: rows of different sheets that share a value in the field they are combined
/// on become one record (one source row), whose values are what each sheet gave. A record is built from at most one row of each sheet, and the
/// first sheet that has a value for a field decides it; a second row of the same sheet with the same value is a record of its own (nothing is
/// silently dropped). A row with no value to combine on, and every row of a sheet that is not combined on anything, is a record by itself.
/// Each sheet is read with its own mapping: its filter and virtual columns apply first, then the values go to the destination fields they are
/// mapped to. The records come out in the order their first row appeared, numbered like a row of "every sheet" (the sheet and row number of the
/// first row), so the details file says where each came from. The sheets are read in full on the first read, so this holds one value per field
/// per record in memory (at most <see cref="MaxRecords"/> records).</summary>
public sealed class ImportJoinedChunkReader(
    IReadOnlyList<ImportPlan> members, ImportPlan joined, IImportFileAccess files, FormulaEngine engine) : IImportChunkReader
{
    public const int MaxRecords = 1_000_000;

    private sealed class Record(long id, object?[] values, int sheet)
    {
        public long Id { get; } = id;
        public object?[] Values { get; } = values;
        /// <summary>The sheets this record already has a row from (one bit each).</summary>
        public ulong Sheets { get; set; } = 1UL << sheet;
    }

    private List<Record>? _records;
    private int _next;

    /// <summary>The rows the sheets had (before they were combined into records): what the run reports as read. Known after the first read.</summary>
    public long SourceRows { get; private set; }

    public async Task<ImportSourceReader.Chunk> ReadChunkAsync(ImportPlan plan, long afterId, long maxId, CancellationToken ct)
    {
        _records ??= await BuildAsync(ct);
        var rows = new List<IReadOnlyDictionary<string, object?>>(Math.Min(ImportSourceReader.ChunkSize, _records.Count - _next));
        var columns = joined.Mappings.Select(m => m.SourceColumn!).ToArray();
        for (; _next < _records.Count && rows.Count < ImportSourceReader.ChunkSize; _next++)
        {
            var record = _records[_next];
            var row = new Dictionary<string, object?>(columns.Length + 1) { ["Id"] = record.Id };
            for (var u = 0; u < columns.Length; u++) row[columns[u]] = record.Values[u];
            rows.Add(row);
        }
        return new ImportSourceReader.Chunk(rows, _next, rows.Count == 0);
    }

    private async Task<List<Record>> BuildAsync(CancellationToken ct)
    {
        var width = joined.Mappings.Count;
        var placeOf = joined.Mappings.Select((m, i) => (Fid: m.Destination.Fid!.Value, Place: i)).ToDictionary(x => x.Fid, x => x.Place);
        var records = new List<Record>();
        var byKey = new Dictionary<string, Record>(StringComparer.OrdinalIgnoreCase);

        for (var sheet = 0; sheet < members.Count; sheet++)
        {
            var member = members[sheet];
            var map = member.Mappings.Select(m => (Place: placeOf[m.Destination.Fid!.Value], Column: m.SourceColumn!)).ToArray();
            var linkColumn = member.JoinFieldFid is { } link ? member.Mappings.First(m => m.Destination.Fid == link).SourceColumn : null;
            await using var reader = await files.OpenChunkReaderAsync(member, ct);
            long cursor = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = await reader.ReadChunkAsync(member, cursor, member.File?.LastRow ?? long.MaxValue, ct);
                if (chunk.Exhausted) break;
                cursor = chunk.LastId;
                SourceRows += chunk.Rows.Count;
                IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = chunk.Rows;
                if (member.TableFilter is { } filter) rows = rows.Where(filter.Matches).ToList();
                rows = ImportVirtualColumnReader.Apply(member, engine, rows);

                foreach (var row in rows)
                {
                    var values = new object?[width];
                    foreach (var (place, column) in map) values[place] = row.GetValueOrDefault(column);
                    var key = linkColumn is null ? null : KeyOf(row.GetValueOrDefault(linkColumn));
                    if (key is not null && byKey.TryGetValue(key, out var existing) && (existing.Sheets & (1UL << sheet)) == 0)
                    {
                        // The same record seen in another sheet: it takes what it still lacks.
                        for (var i = 0; i < width; i++) if (IsBlank(existing.Values[i]) && !IsBlank(values[i])) existing.Values[i] = values[i];
                        existing.Sheets |= 1UL << sheet;
                        continue;
                    }
                    var record = new Record(ImportFileSheets.RowId(sheet, Convert.ToInt64(row["Id"])), values, sheet);
                    if (key is not null) byKey.TryAdd(key, record);
                    records.Add(record);
                    if (records.Count > MaxRecords)
                        throw new InvalidOperationException($"The sheets make more than {MaxRecords:N0} records, which is more than can be combined in one run. Import them in smaller files.");
                }
            }
        }
        return records;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask; // each sheet's reader is closed as soon as it has been read

    private static bool IsBlank(object? value) => value is null || (value is string s && string.IsNullOrWhiteSpace(s));

    private static string? KeyOf(object? value) => value is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}
