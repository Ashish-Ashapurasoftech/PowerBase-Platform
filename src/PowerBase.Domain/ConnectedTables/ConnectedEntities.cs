namespace PowerBase.Domain.ConnectedTables;

/// <summary>A saved credential that reads one source app (tenant DB, connected.Connection).</summary>
public class ConnectedConnection
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long OwnerUserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string AuthMode { get; set; } = ConnectedAuthModes.UserToken;
    public string RealmSlug { get; set; } = string.Empty;
    public long SourceTenantId { get; set; }
    public Guid SourceAppPublicId { get; set; }
    public string SourceAppName { get; set; } = string.Empty;
    public long RunAsUserId { get; set; }
    public string? UserTokenHash { get; set; }
    public string? UserTokenPrefix { get; set; }
    public string? AppTokenHash { get; set; }
    public string Status { get; set; } = ConnectedConnectionStatuses.Active;
    public string? StatusReason { get; set; }
    public DateTime? LastVerifiedOn { get; set; }
    public DateTime? LastUsedOn { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedOn { get; set; }
    public DateTime? ModifiedOn { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

/// <summary>The connected-table definition attached to an ordinary destination table (tenant DB, connected.ConnectedTable).</summary>
public class ConnectedTable
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long AppId { get; set; }
    public long AppTableId { get; set; }
    public long ConnectionId { get; set; }
    public long OwnerUserId { get; set; }
    public long SourceTenantId { get; set; }
    public Guid SourceAppPublicId { get; set; }
    public Guid SourceTablePublicId { get; set; }
    public string SourceTableName { get; set; } = string.Empty;
    public bool IsCrossTenant { get; set; }
    public int RefreshKeySourceFid { get; set; } = ConnectedSystemFids.RecordId;
    public bool RefreshKeyIsAuto { get; set; } = true;
    public string UpdateMode { get; set; } = ConnectedUpdateModes.Mirror;
    public bool FilterEnabled { get; set; }
    public string? FilterJson { get; set; }
    public string? ScheduleJson { get; set; }
    public DateTime? NextRunOn { get; set; }
    public string TimeZone { get; set; } = "UTC";
    public bool NotifyOnFailure { get; set; } = true;
    public byte MassDeleteGuardPct { get; set; } = 50;
    public int ConsecutiveFailures { get; set; }
    public int MappingVersion { get; set; } = 1;
    /// <summary>Bumped whenever the mapping, filter or refresh key changes: an incremental watermark from an older version is ignored.</summary>
    public int FullRefreshVersion { get; set; } = 1;
    public DateTime? LastSourceWatermark { get; set; }
    public int? WatermarkVersion { get; set; }
    public int IncrementalRunCount { get; set; }
    public string Status { get; set; } = ConnectedTableStatuses.Active;
    public string? AttentionReason { get; set; }
    public DateTime? LastSuccessfulRunOn { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedOn { get; set; }
    public long CreatedBy { get; set; }
    public DateTime? ModifiedOn { get; set; }
    public long? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

/// <summary>One connected field: which source field fills which destination field.</summary>
public class ConnectedField
{
    public long Id { get; set; }
    public long ConnectedTableId { get; set; }
    public long AppFieldId { get; set; }
    public int SourceFid { get; set; }
    public string SourceFieldName { get; set; } = string.Empty;
    public string SourceTypeCode { get; set; } = string.Empty;
    public string Conversion { get; set; } = ConnectedConversions.Identity;
    public int SortOrder { get; set; }
    public string State { get; set; } = ConnectedFieldStates.Connected;
    public bool IsDeleted { get; set; }
}

/// <summary>Which local record holds which source record.</summary>
public class ConnectedSyncRow
{
    public long ConnectedTableId { get; set; }
    public string SyncKey { get; set; } = string.Empty;
    public long LocalRecordId { get; set; }
    public Guid LocalPublicId { get; set; }
    public byte[] SourceHash { get; set; } = Array.Empty<byte>();
    public long LastSeenRunId { get; set; }
}

/// <summary>One refresh (tenant DB, connected.Run).</summary>
public class ConnectedRun
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long ConnectedTableId { get; set; }
    public string TriggeredBy { get; set; } = ConnectedRunTriggers.Manual;
    public long TriggeredByUserId { get; set; }
    public string Status { get; set; } = ConnectedRunStatuses.Queued;
    public bool IsIncremental { get; set; }
    public bool ConfirmMassDelete { get; set; }
    public bool CancelRequested { get; set; }
    public long RowsRead { get; set; }
    public long Inserted { get; set; }
    public long Updated { get; set; }
    public long Unchanged { get; set; }
    public long Reverted { get; set; }
    public long Deleted { get; set; }
    public long Skipped { get; set; }
    public long Errored { get; set; }
    public long? HeldDeleteCount { get; set; }
    public long? SourceMaxId { get; set; }
    public string SnapshotJson { get; set; } = "{}";
    public string? ErrorDetail { get; set; }
    public DateTime QueuedOn { get; set; }
    public DateTime? StartedOn { get; set; }
    public DateTime? CompletedOn { get; set; }
}

public class ConnectedRunIssue
{
    public long Id { get; set; }
    public long RunId { get; set; }
    public long? SourceRecordId { get; set; }
    public int? SourceFid { get; set; }
    public string Outcome { get; set; } = ConnectedIssueOutcomes.Skipped;
    public string ReasonCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

/// <summary>Tenant sharing policy (control DB). A tenant without a row uses the defaults.</summary>
public class ConnectedSharingPolicy
{
    public long TenantId { get; set; }
    public string OutboundPolicy { get; set; } = ConnectedOutboundPolicies.ApprovedOnly;
    public bool InboundEnabled { get; set; } = true;
    public DateTime ModifiedOn { get; set; }
    public long ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

/// <summary>Consent from a provider tenant to a consumer tenant (control DB).</summary>
public class ConnectedDataShare
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long ProviderTenantId { get; set; }
    public long ConsumerTenantId { get; set; }
    public string Scope { get; set; } = ConnectedDataShareScopes.AllApps;
    public string? AppPublicIdsJson { get; set; }
    public bool AllowEncryptedFields { get; set; }
    public bool AllowCrossRegion { get; set; }
    public string Status { get; set; } = ConnectedDataShareStatuses.Requested;
    public long RequestedByUserId { get; set; }
    public DateTime RequestedOn { get; set; }
    public string? RequestNote { get; set; }
    public long? DecidedByUserId { get; set; }
    public DateTime? DecidedOn { get; set; }
    public DateTime? ExpiresOn { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

/// <summary>Registry row: one connected table reading one provider table (control DB).</summary>
public class ConnectedLink
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long ConsumerTenantId { get; set; }
    public Guid ConsumerConnectedTablePublicId { get; set; }
    public string ConsumerTableName { get; set; } = string.Empty;
    public long ProviderTenantId { get; set; }
    public Guid ProviderAppPublicId { get; set; }
    public Guid ProviderTablePublicId { get; set; }
    public string ProviderTableName { get; set; } = string.Empty;
    public long RunAsUserId { get; set; }
    public string Status { get; set; } = ConnectedLinkStatuses.Active;
    public DateTime? LastRunOn { get; set; }
    public long? LastRowCount { get; set; }
    public DateTime CreatedOn { get; set; }
}

/// <summary>A leased item of the control-DB run queue.</summary>
public class ConnectedQueueItem
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public Guid RunPublicId { get; set; }
    public long SourceTenantId { get; set; }
    public Guid ClaimToken { get; set; }
    public int AttemptCount { get; set; }
}
