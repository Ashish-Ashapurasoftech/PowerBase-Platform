using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Imports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Repositories;
using PowerBase.Infrastructure.Services;

namespace PowerBase.Infrastructure.Imports;

/// <summary>Set-based destination access for imports: one bulk copy per chunk instead of one statement per row.</summary>
public sealed class ImportDataStore : TenantRepositoryBase, IImportDataStore
{
    private const int LookupBatch = 1000;
    private const string StageTable = "#import_stage";
    private readonly IEncryptionService _encryption;
    private readonly IMessagePublisher _publisher;
    private readonly ILogger<ImportDataStore> _logger;
    private bool _searchIndexingFailed;

    public ImportDataStore(ITenantConnectionFactory connections, IQueryContext context, IEncryptionService encryption,
        IMessagePublisher publisher, ILogger<ImportDataStore> logger)
        : base(connections, context)
    {
        _encryption = encryption;
        _publisher = publisher;
        _logger = logger;
    }

    public async Task<long> GetMaxRecordIdAsync(AppTable table, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT ISNULL(MAX(Id), 0) FROM {PhysicalNaming.FullTableName(table.Id)} WHERE IsDeleted = 0", cancellationToken: ct));
    }

    public async Task<Dictionary<string, long>> FindRecordIdsAsync(
        AppTable table, AppField field, IReadOnlyCollection<object> values, CancellationToken ct = default)
    {
        var found = new Dictionary<string, long>(ImportKey.Comparer);
        if (values.Count == 0) return found;

        var tableName = PhysicalNaming.FullTableName(table.Id);
        var column = PhysicalNaming.GetPhysicalColumnName(field);
        var isRecordId = ImportTypeCompatibility.IsRecordId(field);
        await using var conn = await ConnectionFactory.CreateAsync(ct);

        // Long-text unique fields are indexed by a SHA-256 digest column, not the value: look up by digest so each
        // chunk is an index seek instead of a scan of the whole column.
        var digestColumn = $"{column}_unique_hash";
        var hasDigest = !isRecordId && await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT COL_LENGTH(@table, @column)", new { table = tableName, column = digestColumn }, cancellationToken: ct)) is not null;

        // The Record ID# is numeric: values that are not whole numbers cannot match any record.
        IEnumerable<object> lookup = isRecordId
            ? values.Select(v => decimal.TryParse(ImportKey.Normalize(v), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) && d == decimal.Truncate(d) ? (object)(long)d : null!)
                .Where(v => v is not null)
            : values;

        foreach (var batch in lookup.Chunk(LookupBatch))
        {
            IEnumerable<(object? Key, long Id)> hits;
            if (hasDigest)
            {
                var hashes = batch.Select(v => Digest(Convert.ToString(v, CultureInfo.InvariantCulture)!)).ToList();
                hits = await conn.QueryAsync<(object? Key, long Id)>(new CommandDefinition(
                    $"SELECT {column} AS [Key], Id FROM {tableName} WHERE IsDeleted = 0 AND {digestColumn} IN @hashes", new { hashes }, cancellationToken: ct));
            }
            else
            {
                hits = await conn.QueryAsync<(object? Key, long Id)>(new CommandDefinition(
                    $"SELECT {column} AS [Key], Id FROM {tableName} WHERE IsDeleted = 0 AND {column} IN @batch", new { batch }, cancellationToken: ct));
            }
            foreach (var (key, id) in hits)
                if (ImportKey.Normalize(key) is { } normalized) found[normalized] = id;
        }
        return found;
    }

    public async Task InsertAsync(
        AppTable table, IReadOnlyList<AppField> fields, IReadOnlyList<IReadOnlyDictionary<long, object?>> rows,
        long createdBy, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        // Rows may set different fields (a blank skipped by "Ignore blanks" leaves that field out): a field a row does
        // not set is stored as NULL, exactly like a field the import never mapped.
        var written = WrittenFields(fields, rows.SelectMany(r => r.Keys));

        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.OpenAsync(ct);
        var enc = await ResolveEncryptionAsync(conn, table, written, ct);

        var data = new DataTable();
        data.Columns.Add("PublicId", typeof(Guid));
        data.Columns.Add("CreatedBy", typeof(long));
        foreach (var f in written) data.Columns.Add(PhysicalNaming.ColumnName(f.Fid!.Value), typeof(object));

        var publicIds = new List<Guid>(rows.Count);
        foreach (var row in rows)
        {
            var publicId = Guid.NewGuid();
            publicIds.Add(publicId);
            var dr = data.NewRow();
            dr["PublicId"] = publicId;
            dr["CreatedBy"] = createdBy;
            await FillValuesAsync(dr, enc, fields, written, row, ct);
            data.Rows.Add(dr);
        }

        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            await BulkCopyAsync(conn, tx, PhysicalNaming.FullTableName(table.Id), data, ct);
            await tx.CommitAsync(ct);
        }
        catch (Exception ex) when (IsRowError(ex))
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw new ImportRowRejectedException(Describe(ex));
        }

        await PublishSearchUpdatesAsync(table, fields, rows, publicIds, ct);
    }

    public async Task UpdateAsync(
        AppTable table, IReadOnlyList<AppField> fields, IReadOnlyList<ImportUpdateRow> rows, long modifiedBy, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        var written = WrittenFields(fields, rows.SelectMany(r => r.Values.Keys));
        if (written.Count == 0) return;

        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.OpenAsync(ct);
        var enc = await ResolveEncryptionAsync(conn, table, written, ct);

        // A field some rows leave out (a blank skipped by "Ignore blanks") keeps its stored value for those rows: it gets
        // a 0/1 flag column in the stage table, and the UPDATE only takes the staged value where the flag is 1.
        var columns = written.Select(f => PhysicalNaming.ColumnName(f.Fid!.Value)).ToList();
        var masked = written.Where(f => rows.Any(r => !r.Values.ContainsKey(f.Fid!.Value)))
            .Select(f => PhysicalNaming.ColumnName(f.Fid!.Value)).ToHashSet();

        var data = new DataTable();
        data.Columns.Add("Id", typeof(long));
        foreach (var f in written) data.Columns.Add(PhysicalNaming.ColumnName(f.Fid!.Value), typeof(object));
        foreach (var column in masked) data.Columns.Add(MaskColumn(column), typeof(bool));
        foreach (var row in rows)
        {
            var dr = data.NewRow();
            dr["Id"] = row.RecordId;
            await FillValuesAsync(dr, enc, fields, written, row.Values, ct);
            foreach (var f in written)
            {
                var column = PhysicalNaming.ColumnName(f.Fid!.Value);
                if (masked.Contains(column)) dr[MaskColumn(column)] = row.Values.ContainsKey(f.Fid.Value);
            }
            data.Rows.Add(dr);
        }

        var tableName = PhysicalNaming.FullTableName(table.Id);
        // The stage table copies the destination column types; Id is cast so it does not inherit the identity property.
        var stageColumns = string.Join(", ", columns.Concat(masked.Select(c => $"CAST(0 AS BIT) AS {MaskColumn(c)}")));
        var createStage = $"SELECT TOP 0 CAST(0 AS BIGINT) AS Id, {stageColumns} INTO {StageTable} FROM {tableName}";
        var assignments = columns.Select(c => masked.Contains(c) ? $"t.{c} = CASE WHEN s.{MaskColumn(c)} = 1 THEN s.{c} ELSE t.{c} END" : $"t.{c} = s.{c}");
        var update = $"""
            UPDATE t SET {string.Join(", ", assignments)}, t.ModifiedOn = SYSUTCDATETIME(), t.ModifiedBy = @modifiedBy
            OUTPUT inserted.Id, inserted.PublicId
            FROM {tableName} t JOIN {StageTable} s ON s.Id = t.Id
            WHERE t.IsDeleted = 0
            """;

        IReadOnlyList<(long Id, Guid PublicId)> updated;
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(createStage, transaction: tx, cancellationToken: ct));
            await BulkCopyAsync(conn, tx, StageTable, data, ct);
            updated = (await conn.QueryAsync<(long Id, Guid PublicId)>(
                new CommandDefinition(update, new { modifiedBy }, tx, cancellationToken: ct))).AsList();
            await tx.CommitAsync(ct);
        }
        catch (Exception ex) when (IsRowError(ex))
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw new ImportRowRejectedException(Describe(ex));
        }

        var byId = rows.ToDictionary(r => r.RecordId, r => r.Values);
        var changed = updated.Where(u => byId.ContainsKey(u.Id)).ToList();
        await PublishSearchUpdatesAsync(table, fields, changed.Select(u => byId[u.Id]).ToList(), changed.Select(u => u.PublicId).ToList(), ct);
    }

    public async IAsyncEnumerable<object> StreamColumnValuesAsync(
        AppTable table, AppField field, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var column = PhysicalNaming.GetPhysicalColumnName(field);
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.OpenAsync(ct);
        await using var command = new SqlCommand(
            $"SELECT {column} FROM {PhysicalNaming.FullTableName(table.Id)} WHERE IsDeleted = 0 AND {column} IS NOT NULL", conn) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        while (await reader.ReadAsync(ct))
            yield return reader.GetValue(0);
    }

    public async Task AddRecordCountAsync(long tableId, int count, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE meta.AppTable SET RecordCount = RecordCount + @count WHERE Id = @tableId", new { tableId, count }, cancellationToken: ct));
    }

    private static string MaskColumn(string column) => $"m_{column}";

    /// <summary>The physical columns a batch writes: the given Fids that are real, writable storage columns.</summary>
    private static List<AppField> WrittenFields(IReadOnlyList<AppField> fields, IEnumerable<long> fids)
    {
        var wanted = fids.ToHashSet(); // the rows' fields, de-duplicated across all rows
        return fields.Where(f => f.Fid.HasValue && wanted.Contains(f.Fid.Value) && !f.IsSystem
                                 && !PhysicalNaming.IsComputedTypeCode(f.TypeCode) && !PhysicalNaming.IsRangeTypeCode(f.TypeCode)).ToList();
    }

    private async Task<FieldEncryptionContext> ResolveEncryptionAsync(SqlConnection conn, AppTable table, List<AppField> written, CancellationToken ct)
    {
        var enc = await FieldEncryptionContext.ResolveAsync(conn, table.AppId, QueryContext.TenantId, _encryption, null, ct);
        if (!enc.IsActive && (enc.IsAppEncrypted || written.Any(f => f.IsEncrypted)))
            await enc.EnsureDekAsync(conn, null, ct);
        return enc;
    }

    /// <summary>Writes one row's values into the data row: encrypted where the app requires it, blanks as NULL for
    /// non-text columns (the same rules as a normal record write).</summary>
    private static async Task FillValuesAsync(
        DataRow target, FieldEncryptionContext enc, IReadOnlyList<AppField> allFields, List<AppField> written,
        IReadOnlyDictionary<long, object?> values, CancellationToken ct)
    {
        var stored = await enc.EncryptValuesAsync(allFields, values, ct);
        foreach (var f in written)
        {
            var raw = stored.TryGetValue(f.Fid!.Value, out var v) ? v : values.GetValueOrDefault(f.Fid.Value);
            var blank = raw is null || (raw is string s && s.Length == 0 && !PhysicalNaming.IsTextStoringTypeCode(f.TypeCode));
            target[PhysicalNaming.ColumnName(f.Fid.Value)] = blank ? DBNull.Value : raw!;
        }
    }

    private static async Task BulkCopyAsync(SqlConnection conn, SqlTransaction tx, string destination, DataTable data, CancellationToken ct)
    {
        using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx)
        {
            DestinationTableName = destination, BulkCopyTimeout = 600, BatchSize = data.Rows.Count
        };
        foreach (DataColumn c in data.Columns) bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);
        await bulk.WriteToServerAsync(data, ct);
    }

    /// <summary>Failures caused by the data (constraint, length, type) rather than by the connection or the server.</summary>
    private static bool IsRowError(Exception ex) =>
        ex is SqlException or InvalidOperationException or InvalidCastException or FormatException or OverflowException;

    /// <summary>Same index messages a normal record write publishes, in one batch per chunk. Search indexing is
    /// best-effort: the rows are already committed, so an unreachable search/queue service is logged and skipped
    /// (for the rest of the run) instead of failing an import that has otherwise succeeded.</summary>
    private async Task PublishSearchUpdatesAsync(
        AppTable table, IReadOnlyList<AppField> fields, IReadOnlyList<IReadOnlyDictionary<long, object?>> rows, IReadOnlyList<Guid> publicIds, CancellationToken ct)
    {
        var searchable = fields.Where(f => f.Fid.HasValue && (f.IsSearchable || f.IsFilterable)).ToList();
        if (searchable.Count == 0 || _searchIndexingFailed) return;
        var messages = rows.Select((row, i) => new SearchIndexMessage
        {
            Action = IndexAction.Upsert, TenantId = QueryContext.TenantId, AppId = table.AppId, TableId = table.Id, RecordPublicId = publicIds[i],
            Payload = searchable.Where(f => row.ContainsKey(f.Fid!.Value))
                .ToDictionary(f => f.Fid!.Value.ToString(), f => f.IsSearchable ? row[f.Fid!.Value] : null)
        }).Where(m => m.Payload is { Count: > 0 }).ToList();
        if (messages.Count == 0) return;
        try { await _publisher.PublishBatchAsync(messages, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _searchIndexingFailed = true;
            _logger.LogWarning(ex, "Search indexing is unavailable; imported records in table {TableId} will not be indexed until re-indexed.", table.Id);
        }
    }

    private static byte[] Digest(string value)
    {
        var bytes = Encoding.Unicode.GetBytes(value);
        var input = new byte[bytes.Length + 1];
        input[0] = 0x01;
        bytes.CopyTo(input, 1);
        return SHA256.HashData(input);
    }

    private static string Describe(Exception ex) => ex is SqlException { Number: 2601 or 2627 }
        ? "A value conflicts with an existing record on a unique field."
        : ex.Message;
}
