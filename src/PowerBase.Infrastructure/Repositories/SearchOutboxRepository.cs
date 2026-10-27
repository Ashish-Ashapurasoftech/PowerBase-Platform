using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

/// <summary>meta.SearchOutbox in the current tenant's database. See <see cref="ISearchOutbox"/>.</summary>
public class SearchOutboxRepository : TenantRepositoryBase, ISearchOutbox
{
    private readonly ILogger<SearchOutboxRepository>? _logger;

    // 0 = not checked, 1 = table exists, 2 = missing (re-checked every minute). Per process: the table is created by a migration.
    private static int _tableState;
    private static long _tableCheckedTicks;

    private const int InsertChunk = 400;   // 5 parameters per row, well under SQL Server's 2,100
    private const int AckChunk = 400;      // 4 parameters per row

    public SearchOutboxRepository(ITenantConnectionFactory connectionFactory, IQueryContext queryContext, ILogger<SearchOutboxRepository>? logger = null)
        : base(connectionFactory, queryContext)
    {
        _logger = logger;
    }

    private static async Task<bool> TableExistsAsync(IDbConnection connection, IDbTransaction? tx, CancellationToken ct)
    {
        var state = Volatile.Read(ref _tableState);
        if (state == 1) return true;
        if (state == 2 && DateTime.UtcNow.Ticks - Interlocked.Read(ref _tableCheckedTicks) < TimeSpan.FromMinutes(1).Ticks) return false;

        var exists = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT CASE WHEN OBJECT_ID('meta.SearchOutbox') IS NULL THEN 0 ELSE 1 END", transaction: tx, cancellationToken: ct)) == 1;
        Interlocked.Exchange(ref _tableCheckedTicks, DateTime.UtcNow.Ticks);
        Volatile.Write(ref _tableState, exists ? 1 : 2);
        return exists;
    }

    public async Task AddAsync(IReadOnlyCollection<SearchIndexMessage> changes, IDbTransaction? transaction, CancellationToken ct = default)
    {
        var rows = changes.Where(c => c.RecordPublicId != Guid.Empty && c.Action is IndexAction.Upsert or IndexAction.Delete).ToList();
        if (rows.Count == 0) return;

        if (transaction?.Connection != null)
        {
            // Same transaction as the record write: a failure here must fail the write, so the two can never disagree.
            await InsertAsync(transaction.Connection, transaction, rows, ct);
            return;
        }

        try
        {
            await using var connection = await ConnectionFactory.CreateAsync(ct);
            await connection.OpenAsync(ct);
            await InsertAsync(connection, null, rows, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The record is already saved; do not turn that into an error for the caller. The daily sweep and a backfill cover the gap.
            _logger?.LogError(ex, "Could not record {Count} search index change(s) in the outbox (no transaction was supplied, so the record write is not rolled back).", rows.Count);
        }
    }

    private static async Task InsertAsync(IDbConnection connection, IDbTransaction? tx, List<SearchIndexMessage> rows, CancellationToken ct)
    {
        if (!await TableExistsAsync(connection, tx, ct)) return;

        foreach (var chunk in rows.Chunk(InsertChunk))
        {
            var parameters = new DynamicParameters();
            var tuples = new List<string>(chunk.Length);
            for (var i = 0; i < chunk.Length; i++)
            {
                var c = chunk[i];
                tuples.Add($"(@t{i}, @a{i}, @r{i}, @x{i}, @v{i})");
                parameters.Add($"t{i}", c.TableId);
                parameters.Add($"a{i}", c.AppId);
                parameters.Add($"r{i}", c.RecordPublicId);
                parameters.Add($"x{i}", (byte)c.Action);
                parameters.Add($"v{i}", c.Version);
            }
            await connection.ExecuteAsync(new CommandDefinition(
                $"INSERT INTO meta.SearchOutbox (TableId, AppId, RecordPublicId, Action, Version) VALUES {string.Join(", ", tuples)}",
                parameters, tx, cancellationToken: ct));
        }
    }

    private const string ClaimSql = """
        ;WITH due AS (
            SELECT TOP (@max) *
            FROM meta.SearchOutbox WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE Attempts < @maxAttempts
              AND CreatedOn <= DATEADD(SECOND, -@graceSeconds, SYSUTCDATETIME())
              AND (LastAttemptOn IS NULL OR LastAttemptOn <= DATEADD(SECOND, -@retrySeconds, SYSUTCDATETIME()))
            ORDER BY Id
        )
        UPDATE due SET Attempts = Attempts + 1, LastAttemptOn = SYSUTCDATETIME()
        OUTPUT inserted.Id, inserted.TableId, inserted.AppId, inserted.RecordPublicId, inserted.Action, inserted.Version, inserted.Attempts;
        """;

    public async Task<IReadOnlyList<SearchOutboxEntry>> ClaimDueAsync(int max, TimeSpan grace, TimeSpan retryAfter, int maxAttempts, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.OpenAsync(ct);
        if (!await TableExistsAsync(connection, null, ct)) return [];

        var rows = await connection.QueryAsync<(long Id, long TableId, long AppId, Guid RecordPublicId, byte Action, long Version, int Attempts)>(
            new CommandDefinition(ClaimSql, new
            {
                max,
                maxAttempts,
                graceSeconds = (int)grace.TotalSeconds,
                retrySeconds = (int)retryAfter.TotalSeconds
            }, cancellationToken: ct));
        return rows.Select(r => new SearchOutboxEntry(r.Id, r.TableId, r.AppId, r.RecordPublicId, (IndexAction)r.Action, r.Version, r.Attempts)).ToList();
    }

    public async Task RecordFailureAsync(IReadOnlyCollection<long> ids, string error, CancellationToken ct = default)
    {
        if (ids.Count == 0) return;
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.OpenAsync(ct);
        if (!await TableExistsAsync(connection, null, ct)) return;
        foreach (var chunk in ids.Chunk(1000))
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE meta.SearchOutbox SET LastError = LEFT(@error, 400) WHERE Id IN @ids",
                new { error, ids = chunk }, cancellationToken: ct));
    }

    public async Task AcknowledgeAsync(long tableId, IReadOnlyList<SearchOutboxAck> acks, CancellationToken ct = default)
    {
        if (acks.Count == 0) return;
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.OpenAsync(ct);
        if (!await TableExistsAsync(connection, null, ct)) return;

        foreach (var chunk in acks.Chunk(AckChunk))
        {
            var parameters = new DynamicParameters();
            parameters.Add("tableId", tableId);
            var tuples = new List<string>(chunk.Length);
            for (var i = 0; i < chunk.Length; i++)
            {
                tuples.Add($"(@r{i}, @v{i}, @u{i}, @d{i})");
                parameters.Add($"r{i}", chunk[i].RecordPublicId);
                parameters.Add($"v{i}", chunk[i].Version);
                parameters.Add($"u{i}", chunk[i].UpTo);
                parameters.Add($"d{i}", chunk[i].IsDelete);
            }

            // A delete confirms every earlier change of the record. An upsert confirms only upserts: all up to its version when the index
            // holds the record's current state, otherwise exactly its own version.
            var sql = $"""
                DELETE o
                FROM meta.SearchOutbox o
                JOIN (VALUES {string.Join(", ", tuples)}) a (RecordPublicId, Version, UpTo, IsDelete)
                  ON o.TableId = @tableId AND o.RecordPublicId = a.RecordPublicId
                 AND ( (a.IsDelete = 1 AND o.Version <= a.Version)
                    OR (a.IsDelete = 0 AND o.Action = 0 AND ((a.UpTo = 1 AND o.Version <= a.Version) OR (a.UpTo = 0 AND o.Version = a.Version))) )
                """;
            await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));
        }
    }

    public async Task<SearchOutboxStats> GetStatsAsync(int maxAttempts, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.OpenAsync(ct);
        if (!await TableExistsAsync(connection, null, ct)) return new SearchOutboxStats(0, 0, null);

        var row = await connection.QuerySingleAsync<(long? Pending, long? Parked, DateTime? Oldest)>(new CommandDefinition("""
            SELECT SUM(CASE WHEN Attempts < @maxAttempts THEN 1 ELSE 0 END),
                   SUM(CASE WHEN Attempts >= @maxAttempts THEN 1 ELSE 0 END),
                   MIN(CASE WHEN Attempts < @maxAttempts THEN CreatedOn END)
            FROM meta.SearchOutbox
            """, new { maxAttempts }, cancellationToken: ct));
        return new SearchOutboxStats(row.Pending ?? 0, row.Parked ?? 0, row.Oldest is { } o ? DateTime.SpecifyKind(o, DateTimeKind.Utc) : null);
    }

    public async Task<int> ReleaseParkedAsync(int maxAttempts, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.OpenAsync(ct);
        if (!await TableExistsAsync(connection, null, ct)) return 0;
        return await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE meta.SearchOutbox SET Attempts = 0 WHERE Attempts >= @maxAttempts", new { maxAttempts }, cancellationToken: ct));
    }
}
