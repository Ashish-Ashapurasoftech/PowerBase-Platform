using System.Collections.Concurrent;
using System.Data;
using Dapper;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Compute;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Repositories;

namespace PowerBase.Infrastructure.Compute;

/// <summary>meta.ComputeOutbox in the current tenant's database. See <see cref="IComputeOutbox"/>.</summary>
public class ComputeOutboxRepository : TenantRepositoryBase, IComputeOutbox
{
    private readonly IComputeSettings _settings;
    private readonly ILogger<ComputeOutboxRepository>? _logger;

    // Per tenant (their databases migrate at different times): 1 = table exists, 2 = missing (re-checked every minute).
    private static readonly ConcurrentDictionary<long, (int State, long CheckedTicks)> TableState = new();

    /// <summary>6 parameters per row, well under SQL Server's 2,100.</summary>
    internal const int InsertChunk = 300;
    private const int IdChunk = 1000;
    private const int PurgeBatch = 5000;

    public ComputeOutboxRepository(ITenantConnectionFactory connectionFactory, IQueryContext queryContext, IComputeSettings settings,
        ILogger<ComputeOutboxRepository>? logger = null)
        : base(connectionFactory, queryContext)
    {
        _settings = settings;
        _logger = logger;
    }

    private async Task<bool> TableExistsAsync(IDbConnection connection, IDbTransaction? tx, CancellationToken ct)
    {
        var tenantId = QueryContext.TenantId;
        if (TableState.TryGetValue(tenantId, out var known))
        {
            if (known.State == 1) return true;
            if (DateTime.UtcNow.Ticks - known.CheckedTicks < TimeSpan.FromMinutes(1).Ticks) return false;
        }

        var exists = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT CASE WHEN OBJECT_ID('meta.ComputeOutbox') IS NULL THEN 0 ELSE 1 END", transaction: tx, cancellationToken: ct)) == 1;
        TableState[tenantId] = (exists ? 1 : 2, DateTime.UtcNow.Ticks);
        return exists;
    }

    public Task<bool> IsEnabledAsync(CancellationToken ct = default) => _settings.IsComputeEnabledAsync(QueryContext.TenantId, ct);

    public async Task<bool> AddAsync(IReadOnlyCollection<ComputeChange> changes, IDbTransaction? transaction, CancellationToken ct = default)
    {
        if (changes.Count == 0) return false;
        if (!await _settings.IsComputeEnabledAsync(QueryContext.TenantId, ct)) return false;

        if (transaction?.Connection != null)
        {
            // Same transaction as the data write: a failure here must fail the write, so the two can never disagree.
            return await InsertAsync(transaction.Connection, transaction, changes, ct);
        }

        try
        {
            await using var connection = await ConnectionFactory.CreateAsync(ct);
            await connection.OpenAsync(ct);
            return await InsertAsync(connection, null, changes, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The data is already saved; do not turn that into an error for the caller. The nightly reconciliation covers the gap.
            _logger?.LogError(ex, "Could not record {Count} compute change(s) in the outbox (no transaction was supplied, so the data write is not rolled back).", changes.Count);
            return false;
        }
    }

    private async Task<bool> InsertAsync(IDbConnection connection, IDbTransaction? tx, IReadOnlyCollection<ComputeChange> changes, CancellationToken ct)
    {
        if (!await TableExistsAsync(connection, tx, ct)) return false;

        foreach (var chunk in changes.Chunk(InsertChunk))
        {
            var (sql, parameters) = BuildInsert(chunk);
            await connection.ExecuteAsync(new CommandDefinition(sql, parameters, tx, cancellationToken: ct));
        }
        return true;
    }

    /// <summary>The INSERT for one chunk of changes. Internal so the unit tests can check the SQL text and parameters.</summary>
    internal static (string Sql, DynamicParameters Parameters) BuildInsert(IReadOnlyList<ComputeChange> chunk)
    {
        var parameters = new DynamicParameters();
        var tuples = new List<string>(chunk.Count);
        for (var i = 0; i < chunk.Count; i++)
        {
            var c = chunk[i];
            tuples.Add($"(@t{i}, @r{i}, @p{i}, @x{i}, @f{i}, @o{i})");
            parameters.Add($"t{i}", c.TableId);
            parameters.Add($"r{i}", c.RecordId);
            parameters.Add($"p{i}", c.RecordPublicId);
            parameters.Add($"x{i}", (byte)c.Reason);
            parameters.Add($"f{i}", ComputeOutboxJson.SerializeFids(c.ChangedFids), DbType.String);
            parameters.Add($"o{i}", ComputeOutboxJson.SerializeOldRefs(c.OldRefs), DbType.String);
        }
        return ($"INSERT INTO meta.ComputeOutbox (TableId, RecordId, RecordPublicId, Reason, ChangedFids, OldRefs) VALUES {string.Join(", ", tuples)}", parameters);
    }

    internal const string ClaimSql = """
        ;WITH due AS (
            SELECT TOP (@max) *
            FROM meta.ComputeOutbox WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE Attempts < @maxAttempts
              AND CreatedOn <= DATEADD(SECOND, -@graceSeconds, SYSUTCDATETIME())
              AND (LastAttemptOn IS NULL OR LastAttemptOn <= DATEADD(SECOND, -@retrySeconds, SYSUTCDATETIME()))
            ORDER BY Id
        )
        UPDATE due SET Attempts = Attempts + 1, LastAttemptOn = SYSUTCDATETIME()
        OUTPUT inserted.Id, inserted.TableId, inserted.Attempts;
        """;

    public async Task<IReadOnlyList<ComputeOutboxEntry>> ClaimDueAsync(int max, TimeSpan grace, TimeSpan retryAfter, int maxAttempts, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        if (!await TableExistsAsync(connection, null, ct)) return [];

        var rows = await connection.QueryAsync<(long Id, long TableId, int Attempts)>(new CommandDefinition(ClaimSql, new
        {
            max,
            maxAttempts,
            graceSeconds = (int)grace.TotalSeconds,
            retrySeconds = (int)retryAfter.TotalSeconds
        }, cancellationToken: ct));
        return rows.Select(r => new ComputeOutboxEntry(r.Id, r.TableId, r.Attempts)).ToList();
    }

    public async Task RecordFailureAsync(IReadOnlyCollection<long> ids, string error, CancellationToken ct = default)
    {
        if (ids.Count == 0) return;
        await using var connection = await OpenConnectionAsync(ct);
        if (!await TableExistsAsync(connection, null, ct)) return;
        foreach (var chunk in ids.Chunk(IdChunk))
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE meta.ComputeOutbox SET LastError = LEFT(@error, 400) WHERE Id IN @ids", new { error, ids = chunk }, cancellationToken: ct));
    }

    public async Task<IReadOnlyDictionary<long, long>> CountPendingByTableAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        if (!await TableExistsAsync(connection, null, ct)) return new Dictionary<long, long>();

        var rows = await connection.QueryAsync<(long TableId, long Pending)>(new CommandDefinition(
            "SELECT TableId, COUNT_BIG(*) AS Pending FROM meta.ComputeOutbox GROUP BY TableId", cancellationToken: ct));
        return rows.ToDictionary(r => r.TableId, r => r.Pending);
    }

    public async Task<long> CountPendingAsync(long tableId, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        if (!await TableExistsAsync(connection, null, ct)) return 0;

        // IX_ComputeOutbox_Table (TableId, Id) answers this without reading other tables' rows.
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT_BIG(*) FROM meta.ComputeOutbox WHERE TableId = @tableId", new { tableId }, cancellationToken: ct));
    }

    public async Task<ComputeOutboxStats> GetStatsAsync(int maxAttempts, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        if (!await TableExistsAsync(connection, null, ct)) return new ComputeOutboxStats(0, 0, null);

        var row = await connection.QuerySingleAsync<(long? Pending, long? Parked, DateTime? Oldest)>(new CommandDefinition("""
            SELECT SUM(CASE WHEN Attempts < @maxAttempts THEN 1 ELSE 0 END),
                   SUM(CASE WHEN Attempts >= @maxAttempts THEN 1 ELSE 0 END),
                   MIN(CASE WHEN Attempts < @maxAttempts THEN CreatedOn END)
            FROM meta.ComputeOutbox
            """, new { maxAttempts }, cancellationToken: ct));
        return new ComputeOutboxStats(row.Pending ?? 0, row.Parked ?? 0, row.Oldest is { } o ? DateTime.SpecifyKind(o, DateTimeKind.Utc) : null);
    }

    public async Task<int> ReleaseParkedAsync(int maxAttempts, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        if (!await TableExistsAsync(connection, null, ct)) return 0;
        return await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE meta.ComputeOutbox SET Attempts = 0 WHERE Attempts >= @maxAttempts", new { maxAttempts }, cancellationToken: ct));
    }

    public async Task<int> PurgeOlderThanAsync(TimeSpan olderThan, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        if (!await TableExistsAsync(connection, null, ct)) return 0;

        // Small batches: never one long transaction, and no lock escalation on the table.
        var total = 0;
        int deleted;
        do
        {
            deleted = await connection.ExecuteAsync(new CommandDefinition(
                "DELETE TOP (@batch) FROM meta.ComputeOutbox WHERE CreatedOn < DATEADD(SECOND, -@seconds, SYSUTCDATETIME())",
                new { batch = PurgeBatch, seconds = (int)olderThan.TotalSeconds }, cancellationToken: ct));
            total += deleted;
        } while (deleted == PurgeBatch && !ct.IsCancellationRequested);
        return total;
    }
}
