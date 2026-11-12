using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.QbMigration;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Services;

namespace PowerBase.Infrastructure.QbMigration;

/// <summary>Writes one chunk of QuickBase records into its PowerBase table, keeping the QuickBase Record IDs.
/// One transaction per chunk: delete whatever an earlier attempt left in the range, bulk copy the rows with their
/// own ids, mark the chunk done, commit. So a chunk can be replayed any number of times (a retry after a commit whose
/// reply was lost gives the same rows, not a duplicate-key failure), and "done" in the ledger always means committed.
/// Encrypted fields are encrypted exactly as a normal record write does, and the load stops if encryption is needed
/// but cannot be confirmed (it never falls back to plain text).</summary>
public sealed class QbmChunkLoader(ITenantConnectionFactory connections, IEncryptionService encryption)
{
    // Namespace for the stable PublicIds of migrated records (RFC 4122 name-based ids).
    private static readonly Guid PublicIdNamespace = new("5b3e8c64-2f4f-4a0e-9a8e-6d1f0c7b2a91");

    public sealed record Result(int RowsWritten, byte[] Checksum);

    /// <summary>The fields a load writes: the given Fids that are real, writable storage columns.</summary>
    public static List<AppField> WrittenFields(IReadOnlyList<AppField> fields, IEnumerable<long> fids)
    {
        var wanted = fids.ToHashSet();
        return fields.Where(f => f.Fid.HasValue && wanted.Contains(f.Fid.Value) && !f.IsSystem && !f.IsDeleted
                                 && !PhysicalNaming.IsComputedTypeCode(f.TypeCode) && !PhysicalNaming.IsRangeTypeCode(f.TypeCode)).ToList();
    }

    public async Task<Result> LoadAsync(long tenantId, AppTable table, IReadOnlyList<AppField> fields, long chunkId, QbRange range,
        IReadOnlyList<QbTransformedRow> rows, CancellationToken ct = default)
    {
        if (rows.Any(r => r.RecordId < range.From || r.RecordId >= range.To))
            throw new InvalidOperationException($"A row is outside chunk range {range.From}..{range.To - 1}.");

        var written = WrittenFields(fields, rows.SelectMany(r => r.Values.Keys));
        await using var conn = await connections.CreateAsync(ct);
        await conn.OpenAsync(ct);
        var enc = await ResolveEncryptionAsync(conn, tenantId, table.AppId, written, ct);

        var data = new DataTable();
        data.Columns.Add("Id", typeof(long));
        data.Columns.Add("PublicId", typeof(Guid));
        data.Columns.Add("IsDeleted", typeof(bool));
        data.Columns.Add("CreatedOn", typeof(DateTime));
        data.Columns.Add("CreatedBy", typeof(long));
        data.Columns.Add("ModifiedOn", typeof(DateTime));
        data.Columns.Add("ModifiedBy", typeof(long));
        foreach (var f in written) data.Columns.Add(PhysicalNaming.ColumnName(f.Fid!.Value), typeof(object));

        foreach (var row in rows)
        {
            var dr = data.NewRow();
            dr["Id"] = row.RecordId;
            dr["PublicId"] = StablePublicId(table.Id, row.RecordId);
            dr["IsDeleted"] = false;
            dr["CreatedOn"] = row.CreatedOn ?? DateTime.UtcNow;
            dr["CreatedBy"] = row.CreatedBy;
            dr["ModifiedOn"] = row.ModifiedOn is { } mo ? mo : DBNull.Value;
            dr["ModifiedBy"] = row.ModifiedBy is { } mb ? mb : DBNull.Value;
            var stored = await enc.EncryptValuesAsync(fields, row.Values, ct);
            foreach (var f in written)
            {
                var raw = stored.TryGetValue(f.Fid!.Value, out var v) ? v : null;
                var blank = raw is null || (raw is string s && s.Length == 0 && !PhysicalNaming.IsTextStoringTypeCode(f.TypeCode));
                dr[PhysicalNaming.ColumnName(f.Fid.Value)] = blank ? DBNull.Value : raw!;
            }
            data.Rows.Add(dr);
        }

        var checksum = Checksum(rows);
        var tableName = PhysicalNaming.FullTableName(table.Id);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                $"DELETE FROM {tableName} WHERE Id >= @from AND Id < @to", new { from = range.From, to = range.To }, tx, cancellationToken: ct));

            using (var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.CheckConstraints, tx)
                   { DestinationTableName = tableName, BulkCopyTimeout = 600, BatchSize = Math.Max(1, data.Rows.Count) })
            {
                foreach (DataColumn c in data.Columns) bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);
                await bulk.WriteToServerAsync(data, ct);
            }

            var marked = await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE qbm.Chunk SET Status = 'done', RowsRead = @count, RowsWritten = @count, Checksum = @checksum,
                       LastError = NULL, FinishedOn = SYSUTCDATETIME()
                WHERE Id = @chunkId AND FromRecordId = @from AND ToRecordId = @to
                """, new { count = rows.Count, checksum, chunkId, from = range.From, to = range.To }, tx, cancellationToken: ct));
            if (marked != 1) throw new InvalidOperationException($"Chunk {chunkId} for range {range.From}..{range.To - 1} is not in the ledger.");

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
        return new Result(rows.Count, checksum);
    }

    /// <summary>Encryption, failing closed: the app's flag is read here without swallowing errors, and the load stops if
    /// a field must be encrypted but no key is active. (FieldEncryptionContext.ResolveAsync treats any error as "not
    /// encrypted", which is fine for reads but would write plain text here.)</summary>
    private async Task<FieldEncryptionContext> ResolveEncryptionAsync(SqlConnection conn, long tenantId, long appId, List<AppField> written, CancellationToken ct)
    {
        var appEncrypted = await conn.QuerySingleOrDefaultAsync<bool?>(new CommandDefinition(
            "SELECT IsEncrypted FROM meta.App WHERE Id = @appId AND IsDeleted = 0", new { appId }, cancellationToken: ct))
            ?? throw new InvalidOperationException($"App {appId} does not exist.");
        var needed = written.Any(f => FieldEncryptionContext.StoresCiphertext(f, appEncrypted));

        var enc = await FieldEncryptionContext.ResolveAsync(conn, appId, tenantId, encryption, null, ct);
        if (enc.IsAppEncrypted != appEncrypted)
            throw new InvalidOperationException("Could not read the app's encryption settings; nothing was written.");
        if (needed && !enc.IsActive) await enc.EnsureDekAsync(conn, null, ct);
        if (needed && !enc.IsActive)
            throw new InvalidOperationException("Fields need encryption but no encryption key is active; nothing was written.");
        return enc;
    }

    /// <summary>The same PublicId every time a record is loaded, so replays and reruns don't change it.</summary>
    public static Guid StablePublicId(long tableId, long recordId)
    {
        var ns = PublicIdNamespace.ToByteArray();
        SwapGuidBytes(ns);
        var name = Encoding.UTF8.GetBytes($"{tableId}:{recordId}");
        var hash = SHA1.HashData([.. ns, .. name]);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50); // version 5
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        SwapGuidBytes(bytes);
        return new Guid(bytes);
    }

    private static void SwapGuidBytes(byte[] b)
    {
        (b[0], b[3]) = (b[3], b[0]);
        (b[1], b[2]) = (b[2], b[1]);
        (b[4], b[5]) = (b[5], b[4]);
        (b[6], b[7]) = (b[7], b[6]);
    }

    /// <summary>SHA-256 over the transformed rows in Record ID order (plain values, before encryption). Validation computes the
    /// same over what PowerBase reads back (decrypted) to prove every value arrived unchanged.</summary>
    public static byte[] Checksum(IEnumerable<QbTransformedRow> rows)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var row in rows.OrderBy(r => r.RecordId))
        {
            var canonical = JsonSerializer.Serialize(new
            {
                id = row.RecordId,
                v = row.Values.OrderBy(kv => kv.Key).Select(kv => new object?[] { kv.Key, QbValueCanonical.Of(kv.Value) }),
            });
            sha.AppendData(Encoding.UTF8.GetBytes(canonical));
            sha.AppendData([10]);
        }
        return sha.GetHashAndReset();
    }
}
