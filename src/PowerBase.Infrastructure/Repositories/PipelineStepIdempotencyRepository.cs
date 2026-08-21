using Dapper;
using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class PipelineStepIdempotencyRepository : TenantRepositoryBase, IPipelineStepIdempotencyRepository
{
    private const string GetByExecutionKeySql = """
        SELECT OutputJson
        FROM meta.PipelineStepIdempotencyLog
        WHERE MessageId = @messageId
          AND StepPublicId = @stepPublicId
          AND ExecutionPathHash = @executionPathHash
        """;

    private const string InsertSql = """
        INSERT INTO meta.PipelineStepIdempotencyLog (MessageId, StepPublicId, ExecutionPathHash, ExecutionPath, OutputJson, CreatedOn)
        VALUES (@messageId, @stepPublicId, @executionPathHash, @executionPath, @outputJson, SYSUTCDATETIME())
        """;

    public PipelineStepIdempotencyRepository(ITenantConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext)
    {
    }

    public async Task<string?> GetByExecutionKeyAsync(Guid messageId, Guid stepPublicId, byte[] executionPathHash, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        var connection = transaction?.Connection ?? await OpenConnectionAsync(ct);
        bool ownConnection = transaction is null;
        try
        {
            return await connection.QuerySingleOrDefaultAsync<string?>(
                new CommandDefinition(GetByExecutionKeySql, new { messageId, stepPublicId, executionPathHash }, transaction, cancellationToken: ct));
        }
        finally
        {
            if (ownConnection) connection.Dispose();
        }
    }

    public async Task InsertAsync(PipelineStepIdempotencyLog log, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        var connection = transaction?.Connection ?? await OpenConnectionAsync(ct);
        bool ownConnection = transaction is null;
        try
        {
            await connection.ExecuteAsync(
                new CommandDefinition(InsertSql, new
                {
                    messageId = log.MessageId,
                    stepPublicId = log.StepPublicId,
                    executionPathHash = log.ExecutionPathHash,
                    executionPath = log.ExecutionPath,
                    outputJson = log.OutputJson
                }, transaction, cancellationToken: ct));
        }
        finally
        {
            if (ownConnection) connection.Dispose();
        }
    }
}
