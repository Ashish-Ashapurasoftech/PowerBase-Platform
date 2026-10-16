namespace PowerBase.Application.Relationships;

/// <summary>
/// Cross-tenant dispatch queue (control DB, see control/014_reference_relink_queue.sql) for linking child records to parent records
/// that were added, or whose key changed, after the children were written. A record's save only queues a job; ReferenceRelinkWorker
/// claims the jobs and does the linking in the background, so the save never waits on the child table.
/// </summary>
public interface IReferenceRelinkQueue
{
    /// <summary>Queues a job for these parent records of the current tenant, on behalf of the current user, once the tenant transaction
    /// in progress commits — nothing is queued if it rolls back — or right away when no transaction is open. A failure to queue is
    /// logged and never fails the save: the children then stay as they were.</summary>
    Task EnqueueAfterCommitAsync(long parentTableId, IReadOnlyCollection<long> parentRecordIds, CancellationToken ct = default);

    /// <summary>Leases pending jobs that are due, and jobs whose lease expired (a crashed or restarted worker).</summary>
    Task<IReadOnlyList<ReferenceRelinkJob>> ClaimAsync(string workerId, int batchSize, int leaseSeconds, CancellationToken ct = default);

    /// <summary>The job is done: its row is removed.</summary>
    Task CompleteAsync(long id, Guid claimToken, CancellationToken ct = default);

    /// <summary>The job failed: due again after a backoff while <paramref name="retry"/>, otherwise kept as Failed.</summary>
    Task FailAsync(long id, Guid claimToken, string error, bool retry, CancellationToken ct = default);

    /// <summary>The job will not be run (e.g. the user who queued it no longer exists): kept as Skipped with the reason.</summary>
    Task SkipAsync(long id, Guid claimToken, string reason, CancellationToken ct = default);
}

/// <summary>A leased job. <see cref="ParentRecordIds"/> is the JSON array of Record IDs the job was queued with.</summary>
public sealed record ReferenceRelinkJob(
    long Id, long TenantId, long ParentTableId, string ParentRecordIds, long RequestedBy, Guid ClaimToken, int AttemptCount);
