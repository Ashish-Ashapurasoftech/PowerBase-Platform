using PowerBase.Domain.Entities;

namespace PowerBase.Application.Imports;

public interface IImportDefinitionRepository
{
    Task<ImportDefinition?> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task<IReadOnlyList<ImportDefinitionListItem>> ListByDestinationAsync(long destinationTableId, CancellationToken ct = default);
    /// <summary>The stored definitions of a destination table, for checking them against the tables' current fields.</summary>
    Task<IReadOnlyList<ImportDefinition>> ListEntitiesByDestinationAsync(long destinationTableId, CancellationToken ct = default);
    Task<long> CreateAsync(ImportDefinition definition, CancellationToken ct = default);
    Task UpdateAsync(ImportDefinition definition, CancellationToken ct = default);
    Task DeleteAsync(long id, long deletedBy, CancellationToken ct = default);
    Task SetAttentionAsync(long id, bool needsAttention, string? reason, CancellationToken ct = default);
    /// <summary>Definitions whose scheduled time has come (oldest first), at most <paramref name="take"/>.</summary>
    Task<IReadOnlyList<ImportDefinition>> ListDueAsync(DateTime nowUtc, int take, CancellationToken ct = default);
    /// <summary>Moves a schedule on to <paramref name="next"/> only if it is still due at <paramref name="expected"/>, so when
    /// two schedulers meet the same definition exactly one of them wins and the import starts once.</summary>
    Task<bool> TryAdvanceScheduleAsync(long id, DateTime expected, DateTime? next, CancellationToken ct = default);
}

public interface IImportFileRepository
{
    Task<long> CreateAsync(ImportFile file, CancellationToken ct = default);
    Task<ImportFile?> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task DeleteAsync(Guid publicId, CancellationToken ct = default);
    Task<int> CountByUserAsync(long userId, CancellationToken ct = default);
    /// <summary>Uploads older than <paramref name="before"/> that were never imported, oldest first.</summary>
    Task<IReadOnlyList<ImportFile>> ListOlderThanAsync(DateTime before, int take, CancellationToken ct = default);
}

public interface IImportRunRepository
{
    /// <summary>Adds the run and returns its id, or 0 when its idempotency key was already used for this import (nothing is added).</summary>
    Task<long> CreateAsync(ImportRun run, CancellationToken ct = default);
    /// <summary>The run an idempotency key was first used for, if any.</summary>
    Task<Guid?> FindPublicIdByKeyAsync(long definitionId, string idempotencyKey, CancellationToken ct = default);
    Task<ImportRun?> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task<IReadOnlyList<ImportRunListItem>> ListByDefinitionAsync(long definitionId, int take, CancellationToken ct = default);
    /// <summary>True when the definition already has a queued or running run (prevents overlapping runs).</summary>
    Task<bool> HasActiveRunAsync(long definitionId, CancellationToken ct = default);
    /// <summary>Marks the run running. The source range is pinned on the first start only.</summary>
    /// <summary>Returns false when the run was cancelled before it could start, in which case it must not run.</summary>
    Task<bool> MarkRunningAsync(long runId, long sourceMaxId, CancellationToken ct = default);
    /// <summary>Adds the chunk's counts to the run and advances the resume cursor — one round trip per chunk.</summary>
    /// <returns>True when a user has asked for the run to stop.</returns>
    Task<bool> AdvanceAsync(long runId, ImportChunkResult chunk, byte progress, CancellationToken ct = default);
    /// <summary>Stops a queued run at once, or flags a running one to stop at its next chunk.</summary>
    Task<ImportCancelOutcome> RequestCancelAsync(long runId, CancellationToken ct = default);
    /// <summary>The user's runs that are active, plus those completed at or after <paramref name="completedSince"/>, with the
    /// database clock the next "since" should use (one round trip).</summary>
    Task<(DateTime ServerTime, IReadOnlyList<ImportRunNotice> Runs)> ListForUserAsync(long userId, DateTime? completedSince, int take, CancellationToken ct = default);
    /// <summary>Adds a sentence to the run's detail text (kept within the column's length). For things learned after the run ended, such as
    /// a completion email that could not be sent.</summary>
    Task AppendDetailAsync(long runId, string note, CancellationToken ct = default);
    /// <summary>Finishes the run. <paramref name="feedbackPath"/> is the stored feedback file, if one was written.</summary>
    Task CompleteAsync(long runId, string status, string? errorDetail, string? feedbackPath = null, CancellationToken ct = default);
    /// <summary>Starts a count for each table a multi-table run fills (index 0 = the import's own table), all at zero.</summary>
    Task InitTargetsAsync(long runId, IReadOnlyList<long> destinationTableIds, CancellationToken ct = default);
    /// <summary>Adds what one or more tables got in a chunk to their counts.</summary>
    Task AddTargetCountsAsync(long runId, IReadOnlyList<ImportTargetCounts> counts, CancellationToken ct = default);
    /// <summary>Sets the run's own counters to the sum of its tables' (so they agree however the run ended).</summary>
    Task SyncTotalsFromTargetsAsync(long runId, CancellationToken ct = default);
    /// <summary>Each table's share of a multi-table run, in the order saved (empty for a run into one table).</summary>
    Task<IReadOnlyList<ImportRunTargetItem>> ListTargetsAsync(long runId, CancellationToken ct = default);
    Task<IReadOnlyList<ImportRunIssueItem>> ListIssuesAsync(long runId, int take, CancellationToken ct = default);
    Task<int> CountIssuesAsync(long runId, CancellationToken ct = default);
    /// <summary>Bulk-inserts issues (stamped with the run id), keeping at most <paramref name="cap"/> per run in the database.</summary>
    Task AddIssuesAsync(long runId, IReadOnlyList<ImportRunIssue> issues, int cap, CancellationToken ct = default);
}

/// <summary>What asking for a run to stop did.</summary>
public enum ImportCancelOutcome
{
    /// <summary>The run had not started: it is cancelled now.</summary>
    Cancelled,
    /// <summary>The run is in progress: it will stop after the chunk it is on.</summary>
    Requested,
    /// <summary>The run had already finished.</summary>
    NotActive
}

/// <summary>Tells the people involved that a run finished. Never throws: a mail problem must not undo an import.</summary>
public interface IImportNotifier
{
    /// <returns>A short plain note when a mail could not be sent (and why), to show on the run; null when everything went out.</returns>
    Task<string?> NotifyAsync(ImportRun finished, ImportRunSnapshot snapshot, CancellationToken ct = default);
}

/// <summary>Counts produced by one committed chunk of source rows.</summary>
public sealed record ImportTargetCounts(byte Index, long Inserted, long Updated, long Skipped, long Errored);

public sealed record ImportChunkResult(long LastSourceId, long RowsRead, long Inserted, long Updated, long Skipped, long Errored);

/// <summary>Cross-tenant dispatch queue (control DB). Only the worker and the run starter use it.</summary>
public interface IImportQueue
{
    Task EnqueueAsync(long tenantId, Guid runPublicId, CancellationToken ct = default);
    Task<IReadOnlyList<ImportQueueItem>> ClaimAsync(string workerId, int batchSize, int leaseSeconds, CancellationToken ct = default);
    Task<bool> RenewLeaseAsync(long id, string workerId, Guid claimToken, int leaseSeconds, CancellationToken ct = default);
    Task CompleteAsync(long id, string workerId, Guid claimToken, string? error, CancellationToken ct = default);
}

public sealed record ImportQueueItem(long Id, long TenantId, Guid RunPublicId, Guid ClaimToken, int AttemptCount);

/// <summary>Thrown by the data store when a bulk write is rejected by a row-level constraint (unique, null, length,
/// type). The engine bisects the chunk to find the offending rows; any other exception is infrastructure and propagates.</summary>
public sealed class ImportRowRejectedException(string message) : Exception(message);

/// <summary>New values for one existing record (Fid → value), identified by its Record ID#.</summary>
public sealed record ImportUpdateRow(long RecordId, IReadOnlyDictionary<long, object?> Values);

/// <summary>Set-based data access used by the import engine (bulk insert/update, lookups). Kept separate from
/// IRecordRepository so the shared record repository is not modified.</summary>
public interface IImportDataStore
{
    /// <summary>Every non-blank value stored in the field's column, streamed (never buffered) so a large table can be
    /// loaded into a compact in-memory set once instead of being queried again for each chunk.</summary>
    IAsyncEnumerable<object> StreamColumnValuesAsync(AppTable table, AppField field, CancellationToken ct = default);

    /// <summary>Highest non-deleted Record ID# in the table (0 when empty). Pins the source range at run start so
    /// an import into its own table never re-reads the rows it just wrote.</summary>
    Task<long> GetMaxRecordIdAsync(AppTable table, CancellationToken ct = default);

    /// <summary>For each of the given values already stored in the field's column, the Record ID# of the record holding
    /// it, keyed by <see cref="ImportKey"/>. The field must be unique (or the Record ID#) so a value maps to one record.</summary>
    Task<Dictionary<string, long>> FindRecordIdsAsync(AppTable table, AppField field, IReadOnlyCollection<object> values, CancellationToken ct = default);

    /// <summary>Inserts all rows with one bulk copy in one transaction. Each row is a Fid → value map. Throws on any
    /// row failure without writing anything, so the caller can bisect to isolate the offending rows.</summary>
    Task InsertAsync(AppTable table, IReadOnlyList<AppField> fields, IReadOnlyList<IReadOnlyDictionary<long, object?>> rows,
        long createdBy, CancellationToken ct = default);

    /// <summary>Updates all rows (same set of fields in every row) with one bulk copy and one UPDATE in one transaction.
    /// Throws <see cref="ImportRowRejectedException"/> without writing anything if the database rejects any row.</summary>
    Task UpdateAsync(AppTable table, IReadOnlyList<AppField> fields, IReadOnlyList<ImportUpdateRow> rows,
        long modifiedBy, CancellationToken ct = default);

    /// <summary>Adds <paramref name="count"/> to the table's cached record count.</summary>
    Task AddRecordCountAsync(long tableId, int count, CancellationToken ct = default);
}
