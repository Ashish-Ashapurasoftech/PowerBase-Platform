using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.ConnectedTables.Interfaces;
using PowerBase.Domain.ConnectedTables;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Repositories;

namespace PowerBase.Infrastructure.ConnectedTables;

/// <summary>Destination writes for a sync. Record rows are written only through the existing <see cref="IRecordRepository"/>
/// (so encryption, blank handling and search indexing behave exactly as for any other write); the module's own key map
/// (connected.SyncRow) is updated inside the same transaction so the two never disagree.</summary>
public sealed class ConnectedDataWriter(
    ITenantConnectionFactory connections,
    IQueryContext context,
    IRecordRepository records,
    IAppTableRepository tables,
    IMessagePublisher publisher)
    : TenantRepositoryBase(connections, context), IConnectedDataWriter
{
    private const int IdBatch = 1000;

    private const string GetSyncRowsSql = """
        SELECT s.ConnectedTableId, s.SyncKey, s.LocalRecordId, s.LocalPublicId, s.SourceHash, s.LastSeenRunId
        FROM connected.SyncRow s
        JOIN OPENJSON(@keys) WITH (k NVARCHAR(450) '$') j ON j.k = s.SyncKey
        WHERE s.ConnectedTableId = @connectedTableId
        """;

    private const string UpsertSyncRowsSql = """
        MERGE connected.SyncRow WITH (HOLDLOCK) AS t
        USING (SELECT k, id, pid, h FROM OPENJSON(@rows) WITH (k NVARCHAR(450), id BIGINT, pid UNIQUEIDENTIFIER, h CHAR(64))) AS s
            ON t.ConnectedTableId = @connectedTableId AND t.SyncKey = s.k
        WHEN MATCHED THEN UPDATE SET LocalRecordId = s.id, LocalPublicId = s.pid, SourceHash = CONVERT(BINARY(32), s.h, 2), LastSeenRunId = @runId
        WHEN NOT MATCHED THEN INSERT (ConnectedTableId, SyncKey, LocalRecordId, LocalPublicId, SourceHash, LastSeenRunId)
            VALUES (@connectedTableId, s.k, s.id, s.pid, CONVERT(BINARY(32), s.h, 2), @runId);
        """;

    private const string StampSyncRowsSql = """
        UPDATE t SET SourceHash = CONVERT(BINARY(32), s.h, 2), LastSeenRunId = @runId
        FROM connected.SyncRow t
        JOIN OPENJSON(@rows) WITH (k NVARCHAR(450), h CHAR(64)) s ON t.SyncKey = s.k
        WHERE t.ConnectedTableId = @connectedTableId
        """;

    private const string DeleteSyncRowsSql = """
        DELETE t FROM connected.SyncRow t
        JOIN OPENJSON(@ids) WITH (pid UNIQUEIDENTIFIER '$') j ON j.pid = t.LocalPublicId
        WHERE t.ConnectedTableId = @connectedTableId
        """;

    private const string CountStaleSql = """
        SELECT COUNT_BIG(1) FROM connected.SyncRow WHERE ConnectedTableId = @connectedTableId AND LastSeenRunId <> @runId
        """;

    private const string StampSeenSql = """
        UPDATE t SET LastSeenRunId = @runId
        FROM connected.SyncRow t
        JOIN OPENJSON(@keys) WITH (k NVARCHAR(450) '$') j ON j.k = t.SyncKey
        WHERE t.ConnectedTableId = @connectedTableId
        """;

    private const string ClearSyncRowsSql ="DELETE FROM connected.SyncRow WHERE ConnectedTableId = @connectedTableId";

    public async Task<IReadOnlyDictionary<string, ConnectedSyncRow>> GetSyncRowsAsync(long connectedTableId, IReadOnlyCollection<string> keys, CancellationToken ct = default)
    {
        var found = new Dictionary<string, ConnectedSyncRow>(StringComparer.Ordinal);
        if (keys.Count == 0) return found;
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        foreach (var batch in keys.Chunk(IdBatch))
        {
            var rows = await conn.QueryAsync<ConnectedSyncRow>(new CommandDefinition(GetSyncRowsSql,
                new { connectedTableId, keys = JsonSerializer.Serialize(batch) }, cancellationToken: ct));
            foreach (var row in rows) found[row.SyncKey] = row;
        }
        return found;
    }

    public async Task<IReadOnlyDictionary<long, IReadOnlyDictionary<string, object?>>> GetLocalRowsAsync(
        AppTable table, IReadOnlyList<AppField> fields, IReadOnlyCollection<long> localIds, CancellationToken ct = default)
    {
        var result = new Dictionary<long, IReadOnlyDictionary<string, object?>>();
        foreach (var batch in localIds.Chunk(IdBatch))
            foreach (var (id, row) in await records.GetRowsByIdsAsync(table, fields, batch, ct))
                result[id] = row;
        return result;
    }

    public async Task WriteChunkAsync(AppTable table, IReadOnlyList<AppField> fields, long connectedTableId, long runId,
        IReadOnlyList<ConnectedInsert> inserts, IReadOnlyList<ConnectedUpdate> updates, IReadOnlyList<ConnectedTouch> touches, CancellationToken ct = default)
    {
        if (inserts.Count == 0 && updates.Count == 0 && touches.Count == 0) return;

        var messages = new List<SearchIndexMessage>();
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            if (inserts.Count > 0)
            {
                var publicIds = await records.CreateManyAsync(table, fields, inserts.Select(i => i.Values).ToList(), tx, ct, messages.Add);
                var idByPublicId = await LocalIdsAsync(conn, tx, table, publicIds, ct);
                var rows = inserts.Select((insert, n) => new
                {
                    k = insert.SyncKey,
                    id = idByPublicId[publicIds[n]],
                    pid = publicIds[n],
                    h = Convert.ToHexString(insert.SourceHash)
                });
                await conn.ExecuteAsync(new CommandDefinition(UpsertSyncRowsSql,
                    new { connectedTableId, runId, rows = JsonSerializer.Serialize(rows) }, tx, cancellationToken: ct));
                await tables.IncrementRecordCountByAsync(table.Id, inserts.Count, tx, ct);
            }

            foreach (var update in updates)
                await records.UpdateAsync(table, fields, update.LocalPublicId, update.Values, tx, ct, messages.Add);

            var stamps = updates.Select(u => new { k = u.SyncKey, h = Convert.ToHexString(u.SourceHash) })
                .Concat(touches.Select(t => new { k = t.SyncKey, h = Convert.ToHexString(t.SourceHash) }))
                .ToList();
            foreach (var batch in stamps.Chunk(IdBatch))
                await conn.ExecuteAsync(new CommandDefinition(StampSyncRowsSql,
                    new { connectedTableId, runId, rows = JsonSerializer.Serialize(batch) }, tx, cancellationToken: ct));

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }

        // Search updates go out only once the rows are really there.
        if (messages.Count > 0) _ = publisher.PublishBatchAsync(messages, CancellationToken.None);
    }

    public async Task<long> CountLiveRecordsAsync(AppTable table, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT_BIG(1) FROM {PhysicalNaming.FullTableName(table.Id)} WHERE IsDeleted = 0", cancellationToken: ct));
    }

    public async Task<long> CountStaleAsync(long connectedTableId, long runId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(CountStaleSql, new { connectedTableId, runId }, cancellationToken: ct));
    }

    public async Task<long> CountUnmanagedAsync(AppTable table, long connectedTableId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition($"""
            SELECT COUNT_BIG(1) FROM {PhysicalNaming.FullTableName(table.Id)} r
            WHERE r.IsDeleted = 0
              AND NOT EXISTS (SELECT 1 FROM connected.SyncRow s WHERE s.ConnectedTableId = @connectedTableId AND s.LocalRecordId = r.Id)
            """, new { connectedTableId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<Guid>> ListStaleAsync(AppTable table, long connectedTableId, long runId, int take, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return (await conn.QueryAsync<Guid>(new CommandDefinition($"""
            SELECT TOP (@take) s.LocalPublicId
            FROM connected.SyncRow s
            WHERE s.ConnectedTableId = @connectedTableId AND s.LastSeenRunId <> @runId
            ORDER BY s.LocalRecordId
            """, new { connectedTableId, runId, take }, cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<Guid>> ListUnmanagedAsync(AppTable table, long connectedTableId, int take, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return (await conn.QueryAsync<Guid>(new CommandDefinition($"""
            SELECT TOP (@take) r.PublicId FROM {PhysicalNaming.FullTableName(table.Id)} r
            WHERE r.IsDeleted = 0
              AND NOT EXISTS (SELECT 1 FROM connected.SyncRow s WHERE s.ConnectedTableId = @connectedTableId AND s.LocalRecordId = r.Id)
            ORDER BY r.Id
            """, new { connectedTableId, take }, cancellationToken: ct))).AsList();
    }

    public async Task DeleteAsync(AppTable table, long connectedTableId, IReadOnlyList<Guid> localPublicIds, CancellationToken ct = default)
    {
        if (localPublicIds.Count == 0) return;
        var messages = new List<SearchIndexMessage>();
        int liveDeleted;
        await using (var conn = await ConnectionFactory.CreateAsync(ct))
        {
            await conn.OpenAsync(ct);
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
            try
            {
                liveDeleted = 0;
                foreach (var batch in localPublicIds.Chunk(IdBatch))
                {
                    // A sync row can point at a record already deleted elsewhere: count only the live ones for RecordCount.
                    liveDeleted += await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                        $"SELECT COUNT(1) FROM {PhysicalNaming.FullTableName(table.Id)} WHERE IsDeleted = 0 AND PublicId IN @ids",
                        new { ids = batch }, tx, cancellationToken: ct));
                    await records.BulkDeleteAsync(table, batch, tx, ct, messages.Add);
                    await conn.ExecuteAsync(new CommandDefinition(DeleteSyncRowsSql,
                        new { connectedTableId, ids = JsonSerializer.Serialize(batch) }, tx, cancellationToken: ct));
                }
                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        if (liveDeleted > 0) await tables.DecrementRecordCountByAsync(table.Id, liveDeleted, ct);
        if (messages.Count > 0) _ = publisher.PublishBatchAsync(messages, CancellationToken.None);
    }

    public async Task StampSeenAsync(long connectedTableId, long runId, IReadOnlyCollection<string> keys, CancellationToken ct = default)
    {
        if (keys.Count == 0) return;
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        foreach (var batch in keys.Chunk(IdBatch))
            await conn.ExecuteAsync(new CommandDefinition(StampSeenSql,
                new { connectedTableId, runId, keys = JsonSerializer.Serialize(batch) }, cancellationToken: ct));
    }

    public async Task ReplaceSyncRowsAsync(long connectedTableId, IReadOnlyList<(string Key, long LocalRecordId, Guid LocalPublicId)> rows, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(ClearSyncRowsSql, new { connectedTableId }, tx, cancellationToken: ct));
        // A zero hash: the next refresh compares values anyway, it only decides "updated" vs "restored" by the hash.
        var zero = new string('0', 64);
        foreach (var batch in rows.Chunk(IdBatch))
            await conn.ExecuteAsync(new CommandDefinition(UpsertSyncRowsSql, new
            {
                connectedTableId,
                runId = 0L,
                rows = JsonSerializer.Serialize(batch.Select(r => new { k = r.Key, id = r.LocalRecordId, pid = r.LocalPublicId, h = zero }))
            }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task ClearSyncRowsAsync(long connectedTableId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(ClearSyncRowsSql, new { connectedTableId }, cancellationToken: ct));
    }

    private static async Task<Dictionary<Guid, long>> LocalIdsAsync(SqlConnection conn, SqlTransaction tx, AppTable table, IReadOnlyList<Guid> publicIds, CancellationToken ct)
    {
        var map = new Dictionary<Guid, long>(publicIds.Count);
        var sql = $"SELECT PublicId, Id FROM {PhysicalNaming.FullTableName(table.Id)} WHERE PublicId IN @ids";
        foreach (var batch in publicIds.Chunk(IdBatch))
            foreach (var (publicId, id) in await conn.QueryAsync<(Guid, long)>(new CommandDefinition(sql, new { ids = batch }, tx, cancellationToken: ct)))
                map[publicId] = id;
        return map;
    }
}
