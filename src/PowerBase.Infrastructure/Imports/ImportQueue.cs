using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Repositories;

namespace PowerBase.Infrastructure.Imports;

/// <summary>Control-DB dispatch queue for import runs (see control/012_import_queue.sql).</summary>
public sealed class ImportQueue(IControlConnectionFactory connections, IQueryContext context)
    : ControlRepositoryBase(connections, context), IImportQueue
{
    public async Task EnqueueAsync(long tenantId, Guid runPublicId, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO meta.ImportQueue (TenantId, RunPublicId) VALUES (@tenantId, @runPublicId)",
            new { tenantId, runPublicId }, cancellationToken: ct));
        ImportQueueWakeNotifier.Wake();
    }

    /// <summary>Leases pending items and items whose lease expired (a crashed or restarted worker).</summary>
    public async Task<IReadOnlyList<ImportQueueItem>> ClaimAsync(string workerId, int batchSize, int leaseSeconds, CancellationToken ct = default)
    {
        const string sql = """
            ;WITH candidates AS (
                SELECT TOP (@batchSize) Id FROM meta.ImportQueue WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE (Status = 'Pending' AND (NextAttemptOn IS NULL OR NextAttemptOn <= SYSUTCDATETIME()))
                   OR (Status = 'Processing' AND LockedUntil < SYSUTCDATETIME())
                ORDER BY CreatedOn, Id
            )
            UPDATE q SET Status = 'Processing', LockedBy = @workerId, ClaimToken = NEWID(), AttemptCount = q.AttemptCount + 1,
                         LockedUntil = DATEADD(second, @leaseSeconds, SYSUTCDATETIME())
            OUTPUT inserted.Id, inserted.TenantId, inserted.RunPublicId, inserted.ClaimToken, inserted.AttemptCount
            FROM meta.ImportQueue q JOIN candidates c ON c.Id = q.Id
            """;
        await using var conn = await OpenNewConnectionAsync(ct);
        return (await conn.QueryAsync<ImportQueueItem>(new CommandDefinition(sql, new { batchSize, workerId, leaseSeconds }, cancellationToken: ct))).AsList();
    }

    public async Task<bool> RenewLeaseAsync(long id, string workerId, Guid claimToken, int leaseSeconds, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE meta.ImportQueue SET LockedUntil = DATEADD(second, @leaseSeconds, SYSUTCDATETIME())
            WHERE Id = @id AND LockedBy = @workerId AND ClaimToken = @claimToken AND Status = 'Processing'
            """, new { id, workerId, claimToken, leaseSeconds }, cancellationToken: ct)) > 0;
    }

    public async Task CompleteAsync(long id, string workerId, Guid claimToken, string? error, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE meta.ImportQueue SET Status = CASE WHEN @error IS NULL THEN 'Done' ELSE 'Failed' END, LastError = @error, LockedUntil = NULL
            WHERE Id = @id AND LockedBy = @workerId AND ClaimToken = @claimToken
            """, new { id, workerId, claimToken, error }, cancellationToken: ct));
    }
}

/// <summary>Wakes the worker as soon as a run is queued instead of waiting for its next poll.</summary>
public static class ImportQueueWakeNotifier
{
    private static readonly SemaphoreSlim Signal = new(0, 1);

    public static void Wake()
    {
        if (Signal.CurrentCount == 0) try { Signal.Release(); } catch (SemaphoreFullException) { }
    }

    public static async Task WaitAsync(TimeSpan timeout, CancellationToken ct) => await Signal.WaitAsync(timeout, ct);
}
