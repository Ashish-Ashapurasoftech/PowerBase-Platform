using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Import.Jobs;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Repositories;

namespace PowerBase.Infrastructure.Imports;

/// <summary>Control-DB dispatch queue for background app imports (control/020_create_app_import_queue.sql). Same lease pattern as
/// <see cref="ImportQueue"/>: UPDLOCK + READPAST claim, a claim token as ownership fence, lease renewal while running.</summary>
public sealed class AppImportDispatchQueue(IControlConnectionFactory connections, IQueryContext context)
    : ControlRepositoryBase(connections, context), IAppImportDispatchQueue
{
    public async Task EnqueueAsync(long tenantId, Guid jobPublicId, AppImportIdentity identity, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO meta.AppImportQueue (TenantId, JobPublicId, IdentityJson) VALUES (@tenantId, @jobPublicId, @identity)",
            new { tenantId, jobPublicId, identity = identity.ToJson() }, cancellationToken: ct));
        AppImportQueueWake.Wake();
    }

    public async Task<AppImportQueueItem?> ClaimAsync(string workerId, int leaseSeconds, CancellationToken ct = default)
    {
        const string sql = """
            ;WITH candidate AS (
                SELECT TOP (1) Id FROM meta.AppImportQueue WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE Status = 'Pending' OR (Status = 'Processing' AND LockedUntil < SYSUTCDATETIME())
                ORDER BY CreatedOn, Id
            )
            UPDATE q SET Status = 'Processing', LockedBy = @workerId, ClaimToken = NEWID(), AttemptCount = q.AttemptCount + 1,
                         LockedUntil = DATEADD(second, @leaseSeconds, SYSUTCDATETIME())
            OUTPUT inserted.Id, inserted.TenantId, inserted.JobPublicId, inserted.IdentityJson, inserted.ClaimToken, inserted.AttemptCount
            FROM meta.AppImportQueue q JOIN candidate c ON c.Id = q.Id
            """;
        await using var conn = await OpenNewConnectionAsync(ct);
        return (await conn.QueryAsync<AppImportQueueItem>(new CommandDefinition(sql, new { workerId, leaseSeconds }, cancellationToken: ct))).FirstOrDefault();
    }

    public async Task<bool> RenewLeaseAsync(long id, string workerId, Guid claimToken, int leaseSeconds, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE meta.AppImportQueue SET LockedUntil = DATEADD(second, @leaseSeconds, SYSUTCDATETIME())
            WHERE Id = @id AND LockedBy = @workerId AND ClaimToken = @claimToken AND Status = 'Processing'
            """, new { id, workerId, claimToken, leaseSeconds }, cancellationToken: ct)) > 0;
    }

    public async Task CompleteAsync(long id, string workerId, Guid claimToken, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE meta.AppImportQueue SET Status = 'Done', LockedUntil = NULL
            WHERE Id = @id AND LockedBy = @workerId AND ClaimToken = @claimToken
            """, new { id, workerId, claimToken }, cancellationToken: ct));
    }
}

/// <summary>Wakes this instance's worker as soon as a job is queued here (other instances find it on their next poll).</summary>
public static class AppImportQueueWake
{
    private static readonly SemaphoreSlim Signal = new(0);
    public static void Wake() { if (Signal.CurrentCount == 0) Signal.Release(); }
    public static Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => Signal.WaitAsync(timeout, ct);
}
