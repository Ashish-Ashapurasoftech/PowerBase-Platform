using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class MainPipelineQueueRepository : ControlRepositoryBase, IMainPipelineQueueRepository
{
    public MainPipelineQueueRepository(IControlConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext)
    {
    }

    public async Task<long> EnqueueAsync(PipelineQueue job, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO meta.PipelineQueue (
                MessageId, TenantId, TenantPublicId, PipelineId, PipelinePublicId, QueueSource,
                TriggerStepId, TriggerStepRefId, TriggerEvent, TriggerPayloadJson, PayloadHash,
                TriggeredBy, TriggerTablePublicId, CorrelationId, Depth, PipelineChain,
                BatchId, VariablesJson, PayloadVersion, EventTimestamp, Status, AttemptCount,
                MaxAttempts, CreatedOn, LastModifiedOn
            )
            OUTPUT inserted.Id
            VALUES (
                @MessageId, @TenantId, @TenantPublicId, @PipelineId, @PipelinePublicId, @QueueSource,
                @TriggerStepId, @TriggerStepRefId, @TriggerEvent, @TriggerPayloadJson, @PayloadHash,
                @TriggeredBy, @TriggerTablePublicId, @CorrelationId, @Depth, @PipelineChain,
                @BatchId, @VariablesJson, @PayloadVersion, @EventTimestamp, 'Pending', 0,
                @MaxAttempts, SYSUTCDATETIME(), SYSUTCDATETIME()
            );
            """;

        try
        {
            if (transaction is not null)
            {
                return await transaction.Connection!.QuerySingleAsync<long>(
                    new CommandDefinition(sql, job, transaction, cancellationToken: ct));
            }

            await using var conn = await OpenNewConnectionAsync(ct);
            return await conn.QuerySingleAsync<long>(new CommandDefinition(sql, job, cancellationToken: ct));
        }
        catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601)
        {
            throw new PowerBase.Infrastructure.Pipelines.DuplicateMessageException(job.MessageId);
        }
    }

    public async Task<IReadOnlyList<PipelineQueue>> ClaimPendingJobsAsync(string workerId, int batchSize, int leaseSeconds, List<long> eligibleTenantIds, CancellationToken ct = default)
    {
        if (eligibleTenantIds == null || eligibleTenantIds.Count == 0)
        {
            return Array.Empty<PipelineQueue>();
        }

        const string sql = """
            ;WITH CandidateJobs AS (
                SELECT TOP (@batchSize) pq.Id
                FROM meta.PipelineQueue pq WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE pq.Status = 'Pending'
                  AND (pq.NextAttemptOn IS NULL OR pq.NextAttemptOn <= SYSUTCDATETIME())
                  AND pq.AttemptCount < pq.MaxAttempts
                  AND pq.TenantId IN @eligibleTenantIds
                ORDER BY pq.CreatedOn ASC, pq.Id ASC
            )
            UPDATE pq
            SET Status = 'Processing',
                LockedBy = @workerId,
                LockedUntil = DATEADD(second, @leaseSeconds, SYSUTCDATETIME()),
                ClaimToken = NEWID(),
                StartedOn = COALESCE(pq.StartedOn, SYSUTCDATETIME()),
                AttemptCount = pq.AttemptCount + 1,
                LastModifiedOn = SYSUTCDATETIME()
            OUTPUT 
                inserted.Id,
                inserted.PublicId,
                inserted.MessageId,
                inserted.TenantId,
                inserted.TenantPublicId,
                inserted.PipelineId,
                inserted.PipelinePublicId,
                inserted.QueueSource,
                inserted.TriggerStepId,
                inserted.TriggerStepRefId,
                inserted.TriggerEvent,
                inserted.TriggerPayloadJson,
                inserted.PayloadHash,
                inserted.TriggeredBy,
                inserted.TriggerTablePublicId,
                inserted.CorrelationId,
                inserted.Depth,
                inserted.PipelineChain,
                inserted.BatchId,
                inserted.VariablesJson,
                inserted.PayloadVersion,
                inserted.EventTimestamp,
                inserted.Status,
                inserted.AttemptCount,
                inserted.MaxAttempts,
                inserted.NextAttemptOn,
                inserted.LockedBy,
                inserted.LockedUntil,
                inserted.ClaimToken,
                inserted.CreatedOn,
                inserted.StartedOn,
                inserted.CompletedOn,
                inserted.FailedOn,
                inserted.SkippedOn,
                inserted.LastModifiedOn,
                inserted.LastError,
                inserted.SkipReason
            FROM meta.PipelineQueue pq
            INNER JOIN CandidateJobs c ON c.Id = pq.Id;
            """;

        await using var conn = await OpenNewConnectionAsync(ct);
        var results = await conn.QueryAsync<PipelineQueue>(
            new CommandDefinition(sql, new { workerId, batchSize, leaseSeconds, eligibleTenantIds }, cancellationToken: ct));
        return results.ToList();
    }

    public async Task<IReadOnlyList<PipelineQueue>> ReclaimExpiredJobsAsync(
        string workerId, int batchSize, int leaseSeconds, List<long> eligibleTenantIds, CancellationToken ct = default)
    {
        const string exhaustionSql = """
            ;WITH ExpiredExhaustedJobs AS (
                SELECT TOP (@batchSize) pq.Id
                FROM meta.PipelineQueue pq WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE pq.Status = 'Processing'
                  AND pq.LockedUntil <= SYSUTCDATETIME()
                  AND pq.AttemptCount >= pq.MaxAttempts
                ORDER BY pq.LockedUntil ASC, pq.Id ASC
            )
            UPDATE pq
            SET Status = 'Failed',
                LockedBy = NULL,
                LockedUntil = NULL,
                ClaimToken = NULL,
                FailedOn = SYSUTCDATETIME(),
                LastError = 'Lease expired and attempts exhausted.',
                LastModifiedOn = SYSUTCDATETIME()
            FROM meta.PipelineQueue pq
            INNER JOIN ExpiredExhaustedJobs c ON c.Id = pq.Id;
            """;

        const string reclaimSql = """
            ;WITH ExpiredJobs AS (
                SELECT TOP (@batchSize) pq.Id
                FROM meta.PipelineQueue pq WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE pq.Status = 'Processing'
                  AND pq.LockedUntil <= SYSUTCDATETIME()
                  AND pq.AttemptCount < pq.MaxAttempts
                  AND pq.TenantId IN @eligibleTenantIds
                ORDER BY pq.LockedUntil ASC, pq.Id ASC
            )
            UPDATE pq
            SET Status = 'Processing',
                LockedBy = @workerId,
                LockedUntil = DATEADD(second, @leaseSeconds, SYSUTCDATETIME()),
                ClaimToken = NEWID(),
                AttemptCount = pq.AttemptCount + 1,
                LastModifiedOn = SYSUTCDATETIME()
            OUTPUT 
                inserted.Id,
                inserted.PublicId,
                inserted.MessageId,
                inserted.TenantId,
                inserted.TenantPublicId,
                inserted.PipelineId,
                inserted.PipelinePublicId,
                inserted.QueueSource,
                inserted.TriggerStepId,
                inserted.TriggerStepRefId,
                inserted.TriggerEvent,
                inserted.TriggerPayloadJson,
                inserted.PayloadHash,
                inserted.TriggeredBy,
                inserted.TriggerTablePublicId,
                inserted.CorrelationId,
                inserted.Depth,
                inserted.PipelineChain,
                inserted.BatchId,
                inserted.VariablesJson,
                inserted.PayloadVersion,
                inserted.EventTimestamp,
                inserted.Status,
                inserted.AttemptCount,
                inserted.MaxAttempts,
                inserted.NextAttemptOn,
                inserted.LockedBy,
                inserted.LockedUntil,
                inserted.ClaimToken,
                inserted.CreatedOn,
                inserted.StartedOn,
                inserted.CompletedOn,
                inserted.FailedOn,
                inserted.SkippedOn,
                inserted.LastModifiedOn,
                inserted.LastError,
                inserted.SkipReason
            FROM meta.PipelineQueue pq
            INNER JOIN ExpiredJobs c ON c.Id = pq.Id;
            """;

        await using var conn = await OpenNewConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(exhaustionSql, new { batchSize }, tx, cancellationToken: ct));
            
            if (eligibleTenantIds == null || eligibleTenantIds.Count == 0)
            {
                await tx.CommitAsync(ct);
                return Array.Empty<PipelineQueue>();
            }

            var results = await conn.QueryAsync<PipelineQueue>(
                new CommandDefinition(reclaimSql, new { workerId, batchSize, leaseSeconds, eligibleTenantIds }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            return results.ToList();
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<bool> RenewLeaseAsync(long id, string workerId, Guid claimToken, int leaseSeconds, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE meta.PipelineQueue
            SET LockedUntil = DATEADD(second, @leaseSeconds, SYSUTCDATETIME()),
                LastModifiedOn = SYSUTCDATETIME()
            WHERE Id = @id AND Status = 'Processing' AND LockedBy = @workerId AND ClaimToken = @claimToken;
            """;

        await using var conn = await OpenNewConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(new CommandDefinition(sql, new { id, workerId, claimToken, leaseSeconds }, cancellationToken: ct));
        return affected > 0;
    }

    public async Task<bool> MarkSucceededAsync(long id, string workerId, Guid claimToken, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE meta.PipelineQueue
            SET Status = 'Succeeded',
                CompletedOn = SYSUTCDATETIME(),
                LockedBy = NULL,
                LockedUntil = NULL,
                ClaimToken = NULL,
                LastModifiedOn = SYSUTCDATETIME()
            WHERE Id = @id AND Status = 'Processing' AND LockedBy = @workerId AND ClaimToken = @claimToken;
            """;

        await using var conn = await OpenNewConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(new CommandDefinition(sql, new { id, workerId, claimToken }, cancellationToken: ct));
        return affected > 0;
    }

    public async Task<bool> MarkSkippedAsync(long id, string workerId, Guid claimToken, string reason, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE meta.PipelineQueue
            SET Status = 'Skipped',
                SkippedOn = SYSUTCDATETIME(),
                SkipReason = @reason,
                LockedBy = NULL,
                LockedUntil = NULL,
                ClaimToken = NULL,
                LastModifiedOn = SYSUTCDATETIME()
            WHERE Id = @id AND Status = 'Processing' AND LockedBy = @workerId AND ClaimToken = @claimToken;
            """;

        await using var conn = await OpenNewConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(new CommandDefinition(sql, new { id, workerId, claimToken, reason }, cancellationToken: ct));
        return affected > 0;
    }

    public async Task<bool> ScheduleRetryAsync(long id, string workerId, Guid claimToken, int backoffSeconds, string error, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE meta.PipelineQueue
            SET Status = CASE WHEN AttemptCount >= MaxAttempts THEN 'Failed' ELSE 'Pending' END,
                NextAttemptOn = CASE WHEN AttemptCount >= MaxAttempts THEN NULL ELSE DATEADD(second, @backoffSeconds, SYSUTCDATETIME()) END,
                FailedOn = CASE WHEN AttemptCount >= MaxAttempts THEN SYSUTCDATETIME() END,
                LastError = @error,
                LockedBy = NULL,
                LockedUntil = NULL,
                ClaimToken = NULL,
                LastModifiedOn = SYSUTCDATETIME()
            WHERE Id = @id AND Status = 'Processing' AND LockedBy = @workerId AND ClaimToken = @claimToken;
            """;

        await using var conn = await OpenNewConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(new CommandDefinition(sql, new { id, workerId, claimToken, backoffSeconds, error }, cancellationToken: ct));
        return affected > 0;
    }

    public async Task<bool> MarkFailedAsync(long id, string workerId, Guid claimToken, string error, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE meta.PipelineQueue
            SET Status = 'Failed',
                FailedOn = SYSUTCDATETIME(),
                LastError = @error,
                LockedBy = NULL,
                LockedUntil = NULL,
                ClaimToken = NULL,
                LastModifiedOn = SYSUTCDATETIME()
            WHERE Id = @id AND Status = 'Processing' AND LockedBy = @workerId AND ClaimToken = @claimToken;
            """;

        await using var conn = await OpenNewConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(new CommandDefinition(sql, new { id, workerId, claimToken, error }, cancellationToken: ct));
        return affected > 0;
    }

    public async Task<int> SweepExhaustedPendingJobsAsync(CancellationToken ct = default)
    {
        const string sql = """
            UPDATE meta.PipelineQueue
            SET Status = 'Failed',
                FailedOn = SYSUTCDATETIME(),
                LastError = 'Max delivery attempts exceeded before claim processing completed.',
                LockedBy = NULL,
                LockedUntil = NULL,
                ClaimToken = NULL,
                LastModifiedOn = SYSUTCDATETIME()
            WHERE Status = 'Pending'
              AND AttemptCount >= MaxAttempts;
            """;

        await using var conn = await OpenNewConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));
    }

    public async Task<int> PruneQueueBatchAsync(int olderThanDays, int batchSize, CancellationToken ct = default)
    {
        const string sql = """
            DELETE TOP (@batchSize) FROM meta.PipelineQueue
            WHERE (Status = 'Succeeded' AND CompletedOn <= DATEADD(day, -@olderThanDays, SYSUTCDATETIME()))
               OR (Status = 'Failed' AND FailedOn <= DATEADD(day, -@olderThanDays, SYSUTCDATETIME()))
               OR (Status = 'Skipped' AND SkippedOn <= DATEADD(day, -@olderThanDays, SYSUTCDATETIME()));
            """;

        await using var conn = await OpenNewConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(sql, new { olderThanDays, batchSize }, cancellationToken: ct));
    }

    public async Task<PipelineQueue?> GetByMessageIdAsync(Guid messageId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM meta.PipelineQueue WHERE MessageId = @messageId;";
        await using var conn = await OpenNewConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<PipelineQueue>(new CommandDefinition(sql, new { messageId }, cancellationToken: ct));
    }

    public async Task<int> PausePendingJobsAsync(long tenantId, long pipelineId, DateTime sentinelDate, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE meta.PipelineQueue
            SET PausedNextAttemptOn = NextAttemptOn,
                NextAttemptOn = @sentinelDate,
                LastModifiedOn = SYSUTCDATETIME()
            WHERE TenantId = @tenantId AND PipelineId = @pipelineId AND Status = 'Pending'
              AND (NextAttemptOn IS NULL OR NextAttemptOn <> @sentinelDate);
            """;
        await using var conn = await OpenNewConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(sql, new { tenantId, pipelineId, sentinelDate }, cancellationToken: ct));
    }

    public async Task<int> ResumePendingJobsAsync(long tenantId, long pipelineId, DateTime sentinelDate, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE meta.PipelineQueue
            SET NextAttemptOn = PausedNextAttemptOn,
                PausedNextAttemptOn = NULL,
                LastModifiedOn = SYSUTCDATETIME()
            WHERE TenantId = @tenantId AND PipelineId = @pipelineId AND Status = 'Pending' AND NextAttemptOn = @sentinelDate;
            """;
        await using var conn = await OpenNewConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(sql, new { tenantId, pipelineId, sentinelDate }, cancellationToken: ct));
    }

    public async Task<bool> DeferPendingJobAsync(long id, string workerId, Guid claimToken, int backoffSeconds, DateTime sentinelDate, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE meta.PipelineQueue
            SET Status = 'Pending',
                PausedNextAttemptOn = NULL,
                NextAttemptOn = @sentinelDate,
                LockedBy = NULL,
                LockedUntil = NULL,
                ClaimToken = NULL,
                LastModifiedOn = SYSUTCDATETIME()
            WHERE Id = @id AND Status = 'Processing' AND LockedBy = @workerId AND ClaimToken = @claimToken;
            """;
        await using var conn = await OpenNewConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(new CommandDefinition(sql, new { id, workerId, claimToken, sentinelDate }, cancellationToken: ct));
        return affected > 0;
    }
}

