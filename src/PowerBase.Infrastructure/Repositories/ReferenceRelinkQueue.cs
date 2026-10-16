using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.UOW;

namespace PowerBase.Infrastructure.Repositories;

/// <summary>Control-DB dispatch queue for re-linking child records (see control/014_reference_relink_queue.sql).</summary>
public sealed class ReferenceRelinkQueue(
    IControlConnectionFactory connections, IQueryContext context, ITenantUnitOfWork uow, ILogger<ReferenceRelinkQueue> logger)
    : ControlRepositoryBase(connections, context), IReferenceRelinkQueue
{
    /// <summary>Record IDs per job: the worker reads a job's parents in one go, and a failed job redoes no more than this.</summary>
    private const int MaxIdsPerJob = 1000;

    /// <summary>Seconds before a failed job is due again, times the attempts it has had.</summary>
    private const int BackoffSecondsPerAttempt = 30;

    private const string EnqueueSql = """
        INSERT INTO meta.ReferenceRelinkQueue (TenantId, ParentTableId, ParentRecordIds, RequestedBy)
        VALUES (@tenantId, @parentTableId, @parentRecordIds, @requestedBy)
        """;

    private const string ClaimSql = """
        ;WITH candidates AS (
            SELECT TOP (@batchSize) Id FROM meta.ReferenceRelinkQueue WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE (Status = 'Pending' AND (NextAttemptOn IS NULL OR NextAttemptOn <= SYSUTCDATETIME()))
               OR (Status = 'Processing' AND LockedUntil < SYSUTCDATETIME())
            ORDER BY CreatedOn, Id
        )
        UPDATE q SET Status = 'Processing', LockedBy = @workerId, ClaimToken = NEWID(), AttemptCount = q.AttemptCount + 1,
                     LockedUntil = DATEADD(second, @leaseSeconds, SYSUTCDATETIME())
        OUTPUT inserted.Id, inserted.TenantId, inserted.ParentTableId, inserted.ParentRecordIds, inserted.RequestedBy,
               inserted.ClaimToken, inserted.AttemptCount
        FROM meta.ReferenceRelinkQueue q JOIN candidates c ON c.Id = q.Id
        """;

    private const string CompleteSql = """
        DELETE FROM meta.ReferenceRelinkQueue WHERE Id = @id AND ClaimToken = @claimToken
        """;

    private const string FailSql = """
        UPDATE meta.ReferenceRelinkQueue
        SET Status        = CASE WHEN @retry = 1 THEN 'Pending' ELSE 'Failed' END,
            NextAttemptOn = CASE WHEN @retry = 1 THEN DATEADD(second, @backoffSeconds * AttemptCount, SYSUTCDATETIME()) END,
            LastError = @error, LockedBy = NULL, LockedUntil = NULL
        WHERE Id = @id AND ClaimToken = @claimToken
        """;

    private const string SkipSql = """
        UPDATE meta.ReferenceRelinkQueue
        SET Status = 'Skipped', LastError = @reason, LockedBy = NULL, LockedUntil = NULL
        WHERE Id = @id AND ClaimToken = @claimToken
        """;

    public Task EnqueueAfterCommitAsync(long parentTableId, IReadOnlyCollection<long> parentRecordIds, CancellationToken ct = default)
    {
        if (parentRecordIds.Count == 0) return Task.CompletedTask;

        // Read now, while the request's context is set: the action may run after the caller has moved on.
        var tenantId = QueryContext.TenantId;
        var requestedBy = QueryContext.UserId;
        var batches = parentRecordIds.Distinct().Chunk(MaxIdsPerJob).Select(ids => JsonSerializer.Serialize(ids)).ToList();

        async Task EnqueueAsync()
        {
            try
            {
                // The parent's save has committed by now, so a cancelled request must not drop its job.
                await using var conn = await OpenNewConnectionAsync(CancellationToken.None);
                foreach (var ids in batches)
                    await conn.ExecuteAsync(new CommandDefinition(EnqueueSql,
                        new { tenantId, parentTableId, parentRecordIds = ids, requestedBy }, cancellationToken: CancellationToken.None));
                ReferenceRelinkWakeNotifier.Wake();
            }
            catch (Exception ex)
            {
                // Logged here because the unit of work swallows a post-commit action's failure.
                logger.LogError(ex, "Queueing the re-link of child records to parent table {ParentTableId} (tenant {TenantId}) failed.",
                    parentTableId, tenantId);
            }
        }

        // Same hook the pipeline trigger uses: run once the save commits, never if it rolls back.
        if (uow.Transaction is not null && uow is TriggerPublishingTenantUnitOfWork publishing)
        {
            publishing.RegisterPostCommitAction(EnqueueAsync);
            return Task.CompletedTask;
        }
        return EnqueueAsync();
    }

    public async Task<IReadOnlyList<ReferenceRelinkJob>> ClaimAsync(string workerId, int batchSize, int leaseSeconds, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        return (await conn.QueryAsync<ReferenceRelinkJob>(new CommandDefinition(
            ClaimSql, new { batchSize, workerId, leaseSeconds }, cancellationToken: ct))).AsList();
    }

    public async Task CompleteAsync(long id, Guid claimToken, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(CompleteSql, new { id, claimToken }, cancellationToken: ct));
    }

    public async Task FailAsync(long id, Guid claimToken, string error, bool retry, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(FailSql,
            new { id, claimToken, error = Truncate(error), retry, backoffSeconds = BackoffSecondsPerAttempt }, cancellationToken: ct));
    }

    public async Task SkipAsync(long id, Guid claimToken, string reason, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(SkipSql, new { id, claimToken, reason = Truncate(reason) }, cancellationToken: ct));
    }

    private static string Truncate(string text) => text.Length > 1000 ? text[..1000] : text;
}

/// <summary>Wakes the worker as soon as a job is queued on this server instead of waiting for its next poll.</summary>
public static class ReferenceRelinkWakeNotifier
{
    private static readonly SemaphoreSlim Signal = new(0, 1);

    public static void Wake()
    {
        if (Signal.CurrentCount == 0) try { Signal.Release(); } catch (SemaphoreFullException) { }
    }

    public static async Task WaitAsync(TimeSpan timeout, CancellationToken ct) => await Signal.WaitAsync(timeout, ct);
}
