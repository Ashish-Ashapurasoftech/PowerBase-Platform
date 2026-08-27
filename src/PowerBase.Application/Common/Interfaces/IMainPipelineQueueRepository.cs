using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IMainPipelineQueueRepository
{
    Task<long> EnqueueAsync(PipelineQueue job, IDbTransaction? transaction = null, CancellationToken ct = default);
    Task<IReadOnlyList<PipelineQueue>> ClaimPendingJobsAsync(string workerId, int batchSize, int leaseSeconds, List<long> eligibleTenantIds, CancellationToken ct = default);
    Task<IReadOnlyList<PipelineQueue>> ReclaimExpiredJobsAsync(string workerId, int batchSize, int leaseSeconds, List<long> eligibleTenantIds, CancellationToken ct = default);
    Task<bool> RenewLeaseAsync(long id, string workerId, Guid claimToken, int leaseSeconds, CancellationToken ct = default);
    Task<bool> MarkSucceededAsync(long id, string workerId, Guid claimToken, CancellationToken ct = default);
    Task<bool> MarkSkippedAsync(long id, string workerId, Guid claimToken, string reason, CancellationToken ct = default);
    Task<bool> ScheduleRetryAsync(long id, string workerId, Guid claimToken, int backoffSeconds, string error, CancellationToken ct = default);
    Task<bool> MarkFailedAsync(long id, string workerId, Guid claimToken, string error, CancellationToken ct = default);
    Task<int> SweepExhaustedPendingJobsAsync(CancellationToken ct = default);
    Task<int> PruneQueueBatchAsync(int olderThanDays, int batchSize, CancellationToken ct = default);
    Task<PipelineQueue?> GetByMessageIdAsync(Guid messageId, CancellationToken ct = default);
    Task<int> PausePendingJobsAsync(long tenantId, long pipelineId, DateTime sentinelDate, CancellationToken ct = default);
    Task<int> ResumePendingJobsAsync(long tenantId, long pipelineId, DateTime sentinelDate, CancellationToken ct = default);
    Task<bool> DeferPendingJobAsync(long id, string workerId, Guid claimToken, int backoffSeconds, DateTime sentinelDate, CancellationToken ct = default);
    Task<int> CancelPendingJobsForPipelinesAsync(long tenantId, IEnumerable<long> pipelineIds, string reason, CancellationToken ct = default);
    Task<int> ResumePendingJobsForPipelinesAsync(long tenantId, IEnumerable<long> pipelineIds, DateTime sentinelDate, CancellationToken ct = default);
}

