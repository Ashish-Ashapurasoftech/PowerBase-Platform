using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IPipelineStepIdempotencyRepository
{
    Task<string?> GetByExecutionKeyAsync(Guid messageId, Guid stepPublicId, byte[] executionPathHash, IDbTransaction? transaction = null, CancellationToken ct = default);
    Task InsertAsync(PipelineStepIdempotencyLog log, IDbTransaction? transaction = null, CancellationToken ct = default);

    /// <summary>Reads many receipts in a few queries instead of one per key. The result is keyed by the upper-case hex
    /// of the execution path hash (<see cref="Convert.ToHexString(byte[])"/>); a key without a receipt is simply absent.</summary>
    Task<IReadOnlyDictionary<string, string>> GetManyByExecutionKeysAsync(Guid messageId, Guid stepPublicId, IReadOnlyCollection<byte[]> executionPathHashes, IDbTransaction? transaction = null, CancellationToken ct = default);

    /// <summary>Inserts many receipts with multi-row statements. Same primary key as <see cref="InsertAsync"/>, so a receipt that
    /// already exists fails the whole call exactly like a single insert would.</summary>
    Task InsertManyAsync(IReadOnlyCollection<PipelineStepIdempotencyLog> logs, IDbTransaction? transaction = null, CancellationToken ct = default);
}
