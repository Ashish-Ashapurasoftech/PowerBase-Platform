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

    // SQL Server allows 2,100 parameters per statement; the lookup uses 2 + N, the insert 5 per row.
    private const int ManyLookupChunk = 1000;
    private const int ManyInsertChunk = 300;

    public async Task<IReadOnlyDictionary<string, string>> GetManyByExecutionKeysAsync(
        Guid messageId, Guid stepPublicId, IReadOnlyCollection<byte[]> executionPathHashes, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (executionPathHashes.Count == 0) return result;
        var connection = transaction?.Connection ?? await OpenConnectionAsync(ct);
        bool ownConnection = transaction is null;
        try
        {
            foreach (var chunk in executionPathHashes.Chunk(ManyLookupChunk))
            {
                var parameters = new BulkParameters();
                parameters.Add("messageId", messageId);
                parameters.Add("stepPublicId", stepPublicId);
                var names = new string[chunk.Length];
                for (var i = 0; i < chunk.Length; i++)
                {
                    names[i] = "@h" + i;
                    parameters.Add("h" + i, chunk[i], DbType.Binary, size: chunk[i].Length);
                }
                var sql = $"""
                    SELECT ExecutionPathHash, OutputJson
                    FROM meta.PipelineStepIdempotencyLog
                    WHERE MessageId = @messageId
                      AND StepPublicId = @stepPublicId
                      AND ExecutionPathHash IN ({string.Join(",", names)})
                    """;
                var rows = await connection.QueryAsync<(byte[] ExecutionPathHash, string? OutputJson)>(
                    new CommandDefinition(sql, parameters, transaction, cancellationToken: ct));
                foreach (var (hash, json) in rows)
                    if (json is not null) result[Convert.ToHexString(hash)] = json;
            }
            return result;
        }
        finally
        {
            if (ownConnection) connection.Dispose();
        }
    }

    public async Task InsertManyAsync(IReadOnlyCollection<PipelineStepIdempotencyLog> logs, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        if (logs.Count == 0) return;
        var connection = transaction?.Connection ?? await OpenConnectionAsync(ct);
        bool ownConnection = transaction is null;
        try
        {
            foreach (var chunk in logs.Chunk(ManyInsertChunk))
            {
                var parameters = new BulkParameters();
                var tuples = new string[chunk.Length];
                for (var i = 0; i < chunk.Length; i++)
                {
                    var log = chunk[i];
                    tuples[i] = $"(@m{i}, @s{i}, @h{i}, @p{i}, @o{i}, SYSUTCDATETIME())";
                    parameters.Add("m" + i, log.MessageId);
                    parameters.Add("s" + i, log.StepPublicId);
                    parameters.Add("h" + i, log.ExecutionPathHash, DbType.Binary, size: log.ExecutionPathHash.Length);
                    parameters.Add("p" + i, log.ExecutionPath);
                    parameters.Add("o" + i, log.OutputJson);
                }
                var sql = "INSERT INTO meta.PipelineStepIdempotencyLog (MessageId, StepPublicId, ExecutionPathHash, ExecutionPath, OutputJson, CreatedOn) VALUES "
                    + string.Join(",", tuples);
                await connection.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: ct));
            }
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
