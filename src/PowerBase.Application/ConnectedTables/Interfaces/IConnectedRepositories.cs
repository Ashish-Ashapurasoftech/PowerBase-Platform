using PowerBase.Domain.ConnectedTables;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.ConnectedTables.Interfaces;

/// <summary>Saved connections (tenant DB, connected.[Connection]).</summary>
public interface IConnectedConnectionRepository
{
    Task<ConnectedConnection?> GetByIdAsync(long id, CancellationToken ct = default);
    Task<ConnectedConnection?> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task<IReadOnlyList<ConnectedConnection>> ListByOwnerAsync(long ownerUserId, CancellationToken ct = default);
    Task<(long Id, Guid PublicId)> CreateAsync(ConnectedConnection connection, CancellationToken ct = default);
    /// <summary>Saves name, credentials and status; throws <c>ConcurrencyException</c> when <see cref="ConnectedConnection.RowVersion"/> is stale.</summary>
    Task UpdateAsync(ConnectedConnection connection, CancellationToken ct = default);
    Task SetStatusAsync(long id, string status, string? reason, CancellationToken ct = default);
    Task TouchAsync(long id, bool verified, CancellationToken ct = default);
    Task<int> CountTablesUsingAsync(long id, CancellationToken ct = default);
    Task SoftDeleteAsync(long id, byte[] rowVersion, CancellationToken ct = default);
}

/// <summary>Connected-table definitions, their fields and refresh permissions (tenant DB).</summary>
public interface IConnectedTableRepository
{
    Task<ConnectedTable?> GetByIdAsync(long id, CancellationToken ct = default);
    Task<ConnectedTable?> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task<ConnectedTable?> GetByAppTableIdAsync(long appTableId, CancellationToken ct = default);
    Task<IReadOnlyList<ConnectedTable>> ListByAppAsync(long appId, CancellationToken ct = default);
    Task<IReadOnlyList<ConnectedTable>> ListDueAsync(DateTime nowUtc, int take, CancellationToken ct = default);
    /// <summary>The destination table ids of every live connected table: the read-only write filter's lookup set.</summary>
    Task<IReadOnlyList<(long Id, Guid PublicId)>> ListConnectedAppTablesAsync(CancellationToken ct = default);
    /// <summary>The destination FIDs of a connected table's connected fields (by the destination table's PublicId).</summary>
    Task<IReadOnlyList<int>> ListConnectedFidsAsync(Guid appTablePublicId, CancellationToken ct = default);

    Task<(long Id, Guid PublicId)> CreateAsync(ConnectedTable table, IReadOnlyList<ConnectedField> fields, CancellationToken ct = default);
    /// <summary>Saves the user-editable settings; throws <c>ConcurrencyException</c> when <see cref="ConnectedTable.RowVersion"/> is stale.</summary>
    Task UpdateSettingsAsync(ConnectedTable table, CancellationToken ct = default);
    Task SetStatusAsync(long id, string status, string? reason, CancellationToken ct = default);
    Task SetNextRunAsync(long id, DateTime? nextRunOn, CancellationToken ct = default);
    /// <summary>Pauses the schedule (Manual only) after repeated failures, keeping its settings.</summary>
    Task DisableScheduleAsync(long id, string scheduleJson, string reason, CancellationToken ct = default);
    Task RecordSuccessAsync(long id, DateTime completedOn, DateTime? watermark, int? watermarkVersion, bool incremental, CancellationToken ct = default);
    /// <summary>Counts one more failed run and returns the new consecutive-failure count.</summary>
    Task<int> RecordFailureAsync(long id, CancellationToken ct = default);
    Task SoftDeleteAsync(long id, string status, CancellationToken ct = default);

    Task<IReadOnlyList<ConnectedField>> ListFieldsAsync(long connectedTableId, CancellationToken ct = default);
    Task AddFieldsAsync(long connectedTableId, IReadOnlyList<ConnectedField> fields, CancellationToken ct = default);
    Task SetFieldStateAsync(long connectedFieldId, string state, CancellationToken ct = default);
    Task RemoveFieldAsync(long connectedFieldId, CancellationToken ct = default);

    Task<IReadOnlyList<long>> ListRefreshRoleIdsAsync(long connectedTableId, CancellationToken ct = default);
    Task SetRefreshRoleIdsAsync(long connectedTableId, IReadOnlyCollection<long> appRoleIds, CancellationToken ct = default);
}

/// <summary>Refresh runs and their issues (tenant DB).</summary>
public interface IConnectedRunRepository
{
    Task<(long Id, Guid PublicId)> CreateAsync(ConnectedRun run, CancellationToken ct = default);
    Task<ConnectedRun?> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task<ConnectedRun?> GetActiveForTableAsync(long connectedTableId, CancellationToken ct = default);
    Task<IReadOnlyList<ConnectedRun>> ListByTableAsync(long connectedTableId, int take, CancellationToken ct = default);
    /// <summary>Moves a queued run to running. False when it is no longer queued (cancelled while waiting).</summary>
    Task<bool> MarkRunningAsync(long id, long sourceMaxId, bool incremental, CancellationToken ct = default);
    /// <summary>Puts an interrupted (or transiently failed) running run back to queued, clearing its counters, so it can start over.</summary>
    Task ResetToQueuedAsync(long id, CancellationToken ct = default);
    Task SaveProgressAsync(ConnectedRun run, CancellationToken ct = default);
    Task CompleteAsync(ConnectedRun run, string status, string? error, CancellationToken ct = default);
    Task<bool> RequestCancelAsync(long id, CancellationToken ct = default);
    Task<bool> IsCancelRequestedAsync(long id, CancellationToken ct = default);
    Task AddIssuesAsync(IReadOnlyList<ConnectedRunIssue> issues, CancellationToken ct = default);
    Task<IReadOnlyList<ConnectedRunIssue>> ListIssuesAsync(long runId, int take, CancellationToken ct = default);
}

/// <summary>An insert produced by a sync chunk: the destination values plus the sync bookkeeping.</summary>
public sealed record ConnectedInsert(string SyncKey, byte[] SourceHash, IReadOnlyDictionary<long, object?> Values);

/// <summary>An update of an existing local record: only the connected fields that differ are written.</summary>
public sealed record ConnectedUpdate(string SyncKey, Guid LocalPublicId, byte[] SourceHash, IReadOnlyDictionary<long, object?> Values);

/// <summary>A record whose values already match: only its sync row is stamped with the run.</summary>
public sealed record ConnectedTouch(string SyncKey, byte[] SourceHash);

/// <summary>Destination-side storage for a sync: record writes go through the existing record repository; the sync
/// key map is updated in the same transaction so the two can never disagree.</summary>
public interface IConnectedDataWriter
{
    Task<IReadOnlyDictionary<string, ConnectedSyncRow>> GetSyncRowsAsync(long connectedTableId, IReadOnlyCollection<string> keys, CancellationToken ct = default);
    /// <summary>Live destination rows for the given local record ids (connected field columns only).</summary>
    Task<IReadOnlyDictionary<long, IReadOnlyDictionary<string, object?>>> GetLocalRowsAsync(AppTable table, IReadOnlyList<AppField> fields, IReadOnlyCollection<long> localIds, CancellationToken ct = default);
    /// <summary>Writes one chunk atomically: inserts, updates, sync-row upserts and run stamps.</summary>
    Task WriteChunkAsync(AppTable table, IReadOnlyList<AppField> fields, long connectedTableId, long runId,
        IReadOnlyList<ConnectedInsert> inserts, IReadOnlyList<ConnectedUpdate> updates, IReadOnlyList<ConnectedTouch> touches, CancellationToken ct = default);
    Task<long> CountLiveRecordsAsync(AppTable table, CancellationToken ct = default);
    /// <summary>Local records the run did not see: their source record is gone (or filtered out).</summary>
    Task<long> CountStaleAsync(long connectedTableId, long runId, CancellationToken ct = default);
    /// <summary>Live local records with no sync row: created outside the module.</summary>
    Task<long> CountUnmanagedAsync(AppTable table, long connectedTableId, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> ListStaleAsync(AppTable table, long connectedTableId, long runId, int take, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> ListUnmanagedAsync(AppTable table, long connectedTableId, int take, CancellationToken ct = default);
    /// <summary>Soft-deletes local records and drops their sync rows in one transaction.</summary>
    Task DeleteAsync(AppTable table, long connectedTableId, IReadOnlyList<Guid> localPublicIds, CancellationToken ct = default);
    /// <summary>Marks the sync rows of these keys as seen by the run (incremental refresh: records that still exist but did not change).</summary>
    Task StampSeenAsync(long connectedTableId, long runId, IReadOnlyCollection<string> keys, CancellationToken ct = default);
    /// <summary>Replaces the whole key map (refresh key changed and the new key's values are already in the table).</summary>
    Task ReplaceSyncRowsAsync(long connectedTableId, IReadOnlyList<(string Key, long LocalRecordId, Guid LocalPublicId)> rows, CancellationToken ct = default);
    /// <summary>Forgets every sync row (refresh key changed): the next run re-matches records by the new key.</summary>
    Task ClearSyncRowsAsync(long connectedTableId, CancellationToken ct = default);
}

/// <summary>Tenant facts from the control DB.</summary>
public sealed record ConnectedTenantInfo(long Id, string Slug, string Name, string Status, string ProvisioningState, string? ServerRef, bool IsDeleted)
{
    public bool IsUsable => !IsDeleted && string.Equals(Status, "Active", StringComparison.OrdinalIgnoreCase)
        && string.Equals(ProvisioningState, "Ready", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Control-DB storage: tenant facts, sharing policy, consent and the cross-tenant link registry.</summary>
public interface IConnectedControlRepository
{
    Task<ConnectedTenantInfo?> GetTenantAsync(long tenantId, CancellationToken ct = default);
    Task<ConnectedTenantInfo?> GetTenantBySlugAsync(string slug, CancellationToken ct = default);
    Task<IReadOnlyDictionary<long, string>> GetTenantNamesAsync(IReadOnlyCollection<long> tenantIds, CancellationToken ct = default);

    Task<ConnectedSharingPolicy> GetPolicyAsync(long tenantId, CancellationToken ct = default);
    Task SavePolicyAsync(ConnectedSharingPolicy policy, CancellationToken ct = default);

    Task<ConnectedDataShare?> GetShareAsync(Guid publicId, CancellationToken ct = default);
    /// <summary>The open (requested or approved) share between the pair, if any.</summary>
    Task<ConnectedDataShare?> GetOpenShareAsync(long providerTenantId, long consumerTenantId, CancellationToken ct = default);
    Task<IReadOnlyList<ConnectedDataShare>> ListSharesForProviderAsync(long providerTenantId, CancellationToken ct = default);
    Task<IReadOnlyList<ConnectedDataShare>> ListSharesForConsumerAsync(long consumerTenantId, CancellationToken ct = default);
    Task<(long Id, Guid PublicId)> CreateShareAsync(ConnectedDataShare share, CancellationToken ct = default);
    /// <summary>Saves a decision (approve / reject / revoke, scope, switches, expiry); <c>ConcurrencyException</c> when stale.</summary>
    Task UpdateShareAsync(ConnectedDataShare share, CancellationToken ct = default);

    Task UpsertLinkAsync(ConnectedLink link, CancellationToken ct = default);
    Task<ConnectedLink?> GetLinkAsync(long consumerTenantId, Guid consumerConnectedTablePublicId, CancellationToken ct = default);
    Task<ConnectedLink?> GetLinkByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task<IReadOnlyList<ConnectedLink>> ListLinksForProviderAsync(long providerTenantId, CancellationToken ct = default);
    Task<IReadOnlyList<ConnectedLink>> ListLinksForConsumerAsync(long consumerTenantId, CancellationToken ct = default);
    Task SetLinkStatusAsync(long linkId, string status, CancellationToken ct = default);
    Task SetLinkStatusForPairAsync(long providerTenantId, long consumerTenantId, string fromStatus, string toStatus, CancellationToken ct = default);
    Task RecordLinkRunAsync(long consumerTenantId, Guid consumerConnectedTablePublicId, long rowCount, CancellationToken ct = default);
}

/// <summary>Control-DB dispatch queue for refresh runs (connected.RunQueue).</summary>
public interface IConnectedRunQueue
{
    Task EnqueueAsync(long tenantId, Guid runPublicId, long sourceTenantId, CancellationToken ct = default);
    /// <summary>Leases pending (or lease-expired) items, never more than <paramref name="maxPerProvider"/> processing per source tenant.</summary>
    Task<IReadOnlyList<ConnectedQueueItem>> ClaimAsync(string workerId, int batchSize, int leaseSeconds, int maxPerProvider, CancellationToken ct = default);
    Task<bool> RenewLeaseAsync(long id, string workerId, Guid claimToken, int leaseSeconds, CancellationToken ct = default);
    Task CompleteAsync(long id, string workerId, Guid claimToken, string? error, CancellationToken ct = default);
    /// <summary>Puts a leased item back as pending, to be retried after <paramref name="delay"/>.</summary>
    Task RetryLaterAsync(long id, string workerId, Guid claimToken, TimeSpan delay, string error, CancellationToken ct = default);
}

/// <summary>A per-table lock held for the whole of a refresh, so two workers can never refresh one table at once.</summary>
public interface IConnectedTableLock
{
    /// <summary>The held lock (dispose to release), or null when another refresh holds it.</summary>
    Task<IAsyncDisposable?> TryAcquireAsync(long connectedTableId, CancellationToken ct = default);
}

/// <summary>Checks an app token against the source app, inside a source-tenant scope.</summary>
public interface IConnectedAppTokenReader
{
    Task<bool> IsValidAsync(long appId, string tokenHash, CancellationToken ct = default);
}
