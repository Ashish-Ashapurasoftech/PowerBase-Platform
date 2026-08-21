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
}
