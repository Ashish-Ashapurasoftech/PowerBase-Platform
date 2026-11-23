using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Application.Compute;

namespace PowerBase.Application.Common.Interfaces;

/// <summary>
/// The tenant database's record of stored-column work that is still to be done (meta.ComputeOutbox). All members work on the tenant in
/// the current <see cref="IQueryContext"/>. Every member is a no-op until the table exists (migration 096), so the application can be
/// deployed before the migration has run.
/// </summary>
public interface IComputeOutbox
{
    /// <summary>
    /// Records the changes. Pass the transaction of the data write so the rows commit or roll back with it. Writes nothing when the
    /// feature is off for the tenant (<see cref="IComputeSettings"/>), so a switched-off tenant behaves exactly as before. Without a
    /// transaction the rows are written on their own connection and a failure is logged, not thrown (the data is already saved).
    /// Returns true when rows were written, so the caller knows to publish the queue message once the transaction commits.
    /// </summary>
    Task<bool> AddAsync(IReadOnlyCollection<ComputeChange> changes, IDbTransaction? transaction, CancellationToken ct = default);

    /// <summary>Whether stored-column capture is on for the current tenant. A write path asks before it changes its SQL (an OUTPUT clause
    /// to catch the old parent), so a switched-off tenant runs exactly the SQL it ran before.</summary>
    Task<bool> IsEnabledAsync(CancellationToken ct = default);

    /// <summary>
    /// Takes up to <paramref name="max"/> rows older than <paramref name="grace"/>, under the attempt limit and not tried within
    /// <paramref name="retryAfter"/>, and counts the attempt. Safe to call from several instances at once.
    /// </summary>
    Task<IReadOnlyList<ComputeOutboxEntry>> ClaimDueAsync(int max, TimeSpan grace, TimeSpan retryAfter, int maxAttempts, CancellationToken ct = default);

    Task RecordFailureAsync(IReadOnlyCollection<long> ids, string error, CancellationToken ct = default);

    /// <summary>Rows waiting per table (all tables of the tenant with at least one row).</summary>
    Task<IReadOnlyDictionary<long, long>> CountPendingByTableAsync(CancellationToken ct = default);

    /// <summary>Rows waiting for one table; 0 when the outbox cannot tell (its table does not exist yet).</summary>
    Task<long> CountPendingAsync(long tableId, CancellationToken ct = default);

    Task<ComputeOutboxStats> GetStatsAsync(int maxAttempts, CancellationToken ct = default);

    /// <summary>Makes rows that ran out of attempts eligible again (the daily sweep). Returns how many.</summary>
    Task<int> ReleaseParkedAsync(int maxAttempts, CancellationToken ct = default);

    /// <summary>Deletes rows older than <paramref name="olderThan"/>. Only used while nothing consumes the rows (Phase 2 observe mode).</summary>
    Task<int> PurgeOlderThanAsync(TimeSpan olderThan, CancellationToken ct = default);
}

/// <summary>Sends "table X of tenant Y has work" to the compute queue (session = "{tenantId}:{tableId}").</summary>
public interface IComputeQueuePublisher
{
    Task PublishAsync(ComputeQueueMessage message, CancellationToken ct = default);
    Task PublishBatchAsync(IEnumerable<ComputeQueueMessage> messages, CancellationToken ct = default);
}

/// <summary>The per-tenant switch of the stored-columns feature: the global Compute:Enabled AND the tenant's own flag.</summary>
public interface IComputeSettings
{
    Task<bool> IsComputeEnabledAsync(long tenantId, CancellationToken ct = default);

    /// <summary>Decision Q3: every Lookup / Summary / Formula field is stored (switched on automatically) unless an admin excluded it.</summary>
    bool StoreAllCalculatedFields => true;
}
