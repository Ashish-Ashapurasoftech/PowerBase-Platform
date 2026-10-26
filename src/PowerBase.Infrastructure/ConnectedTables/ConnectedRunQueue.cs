using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.ConnectedTables.Interfaces;
using PowerBase.Domain.ConnectedTables;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Repositories;

namespace PowerBase.Infrastructure.ConnectedTables;

/// <summary>Control-DB dispatch queue for refresh runs (see control/015_connected_schema.sql).</summary>
public sealed class ConnectedRunQueue(IControlConnectionFactory connections, IQueryContext context)
    : ControlRepositoryBase(connections, context), IConnectedRunQueue
{
    private const string EnqueueSql = """
        INSERT INTO connected.RunQueue (TenantId, RunPublicId, SourceTenantId) VALUES (@tenantId, @runPublicId, @sourceTenantId)
        """;

    // Pending items (and items whose lease ran out) are leased oldest first, but never so many that one source tenant
    // has more than @maxPerProvider refreshes reading from it at the same time.
    private const string ClaimSql = """
        ;WITH busy AS (
            SELECT SourceTenantId, COUNT(1) AS Active FROM connected.RunQueue
            WHERE Status = 'Processing' AND LockedUntil >= SYSUTCDATETIME()
            GROUP BY SourceTenantId
        ),
        ranked AS (
            SELECT q.Id, q.SourceTenantId,
                   ROW_NUMBER() OVER (PARTITION BY q.SourceTenantId ORDER BY q.CreatedOn, q.Id) AS Position
            FROM connected.RunQueue q WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE (q.Status = 'Pending' AND (q.NextAttemptOn IS NULL OR q.NextAttemptOn <= SYSUTCDATETIME()))
               OR (q.Status = 'Processing' AND q.LockedUntil < SYSUTCDATETIME())
        ),
        candidates AS (
            SELECT TOP (@batchSize) r.Id FROM ranked r
            LEFT JOIN busy b ON b.SourceTenantId = r.SourceTenantId
            WHERE r.Position + ISNULL(b.Active, 0) <= @maxPerProvider
            ORDER BY r.Position, r.Id
        )
        UPDATE q SET Status = 'Processing', LockedBy = @workerId, ClaimToken = NEWID(), AttemptCount = q.AttemptCount + 1,
                     LockedUntil = DATEADD(second, @leaseSeconds, SYSUTCDATETIME())
        OUTPUT inserted.Id, inserted.TenantId, inserted.RunPublicId, inserted.SourceTenantId, inserted.ClaimToken, inserted.AttemptCount
        FROM connected.RunQueue q JOIN candidates c ON c.Id = q.Id
        """;

    private const string RenewSql = """
        UPDATE connected.RunQueue SET LockedUntil = DATEADD(second, @leaseSeconds, SYSUTCDATETIME())
        WHERE Id = @id AND LockedBy = @workerId AND ClaimToken = @claimToken AND Status = 'Processing'
        """;

    private const string CompleteSql = """
        UPDATE connected.RunQueue SET Status = CASE WHEN @error IS NULL THEN 'Done' ELSE 'Failed' END, LastError = @error, LockedUntil = NULL
        WHERE Id = @id AND LockedBy = @workerId AND ClaimToken = @claimToken
        """;

    private const string RetrySql = """
        UPDATE connected.RunQueue
        SET Status = 'Pending', LastError = @error, LockedBy = NULL, LockedUntil = NULL, ClaimToken = NULL,
            NextAttemptOn = DATEADD(second, @delaySeconds, SYSUTCDATETIME())
        WHERE Id = @id AND LockedBy = @workerId AND ClaimToken = @claimToken
        """;

    public async Task EnqueueAsync(long tenantId, Guid runPublicId, long sourceTenantId, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(EnqueueSql, new { tenantId, runPublicId, sourceTenantId }, cancellationToken: ct));
        ConnectedQueueWakeNotifier.Wake();
    }

    public async Task<IReadOnlyList<ConnectedQueueItem>> ClaimAsync(string workerId, int batchSize, int leaseSeconds, int maxPerProvider, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        return (await conn.QueryAsync<ConnectedQueueItem>(new CommandDefinition(ClaimSql,
            new { workerId, batchSize, leaseSeconds, maxPerProvider = Math.Max(1, maxPerProvider) }, cancellationToken: ct))).AsList();
    }

    public async Task<bool> RenewLeaseAsync(long id, string workerId, Guid claimToken, int leaseSeconds, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(RenewSql, new { id, workerId, claimToken, leaseSeconds }, cancellationToken: ct)) > 0;
    }

    public async Task CompleteAsync(long id, string workerId, Guid claimToken, string? error, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(CompleteSql,
            new { id, workerId, claimToken, error = ConnectedConnectionRepository.Truncate(error, 1000) }, cancellationToken: ct));
    }

    public async Task RetryLaterAsync(long id, string workerId, Guid claimToken, TimeSpan delay, string error, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(RetrySql,
            new { id, workerId, claimToken, delaySeconds = (int)delay.TotalSeconds, error = ConnectedConnectionRepository.Truncate(error, 1000) }, cancellationToken: ct));
        ConnectedQueueWakeNotifier.Wake();
    }
}

/// <summary>Wakes the sync worker as soon as a run is queued instead of waiting for its next poll.</summary>
public static class ConnectedQueueWakeNotifier
{
    private static readonly SemaphoreSlim Signal = new(0, 1);

    public static void Wake()
    {
        if (Signal.CurrentCount == 0) try { Signal.Release(); } catch (SemaphoreFullException) { }
    }

    public static async Task WaitAsync(TimeSpan timeout, CancellationToken ct) => await Signal.WaitAsync(timeout, ct);
}
