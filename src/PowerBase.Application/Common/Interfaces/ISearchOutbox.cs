using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Application.Common.Models;

namespace PowerBase.Application.Common.Interfaces;

/// <summary>One change still waiting to be confirmed in the search index.</summary>
public sealed record SearchOutboxEntry(long Id, long TableId, long AppId, Guid RecordPublicId, IndexAction Action, long Version, int Attempts);

/// <summary>
/// "This record is in the index as of <see cref="Version"/>". <see cref="UpTo"/>: the index holds the record's CURRENT state (it was read
/// from the database), so every earlier change is covered too. Otherwise only the change with exactly this version is confirmed: a
/// payload carries just the fields that change touched, so confirming it must not confirm older changes whose messages were lost.
/// </summary>
public sealed record SearchOutboxAck(Guid RecordPublicId, long Version, bool UpTo, bool IsDelete);

public sealed record SearchOutboxStats(long Pending, long Parked, DateTime? OldestPendingUtc);

/// <summary>A record with a change the index has not confirmed yet.</summary>
public sealed record SearchOutboxPendingRecord(long TableId, long AppId, Guid RecordPublicId);

/// <summary>
/// The tenant database's record of index changes that have not been confirmed yet (meta.SearchOutbox). All members work on the tenant in
/// the current <see cref="IQueryContext"/>. Every member is a no-op until the table exists (migration 086), so the application can be
/// deployed before the migration has run.
/// </summary>
public interface ISearchOutbox
{
    /// <summary>
    /// Records the changes. Pass the transaction of the record write so the rows commit or roll back with it; without one the rows are
    /// written on their own connection and a failure is logged, not thrown (the record is already saved).
    /// </summary>
    Task AddAsync(IReadOnlyCollection<SearchIndexMessage> changes, IDbTransaction? transaction, CancellationToken ct = default);

    /// <summary>
    /// Takes up to <paramref name="max"/> rows that are older than <paramref name="grace"/>, under the attempt limit and not tried within
    /// <paramref name="retryAfter"/>, and counts the attempt. Safe to call from several instances at once.
    /// </summary>
    Task<IReadOnlyList<SearchOutboxEntry>> ClaimDueAsync(int max, TimeSpan grace, TimeSpan retryAfter, int maxAttempts, CancellationToken ct = default);

    Task RecordFailureAsync(IReadOnlyCollection<long> ids, string error, CancellationToken ct = default);

    /// <summary>Deletes the rows the index has confirmed.</summary>
    Task AcknowledgeAsync(long tableId, IReadOnlyList<SearchOutboxAck> acks, CancellationToken ct = default);

    Task<SearchOutboxStats> GetStatsAsync(int maxAttempts, CancellationToken ct = default);

    /// <summary>
    /// Records of the table with a change the index has not confirmed yet: written, but not (or not yet visibly) indexed. Up to
    /// <paramref name="max"/> + 1 of them, so a caller can tell there are more than it can handle. Null when the outbox cannot tell (its
    /// table does not exist yet in this tenant's database).
    /// </summary>
    Task<IReadOnlyList<Guid>?> ListPendingRecordsAsync(long tableId, int max, CancellationToken ct = default);

    /// <summary>The same across every table of the tenant (for global search), newest change first: up to <paramref name="max"/> records.
    /// Null when the outbox cannot tell.</summary>
    Task<IReadOnlyList<SearchOutboxPendingRecord>?> ListPendingRecordsAsync(int max, CancellationToken ct = default);

    /// <summary>Makes rows that ran out of attempts eligible again (the daily sweep). Returns how many.</summary>
    Task<int> ReleaseParkedAsync(int maxAttempts, CancellationToken ct = default);
}
