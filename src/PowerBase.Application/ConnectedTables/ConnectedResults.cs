using PowerBase.Application.ConnectedTables.Scheduling;
using PowerBase.Application.Reports;

namespace PowerBase.Application.ConnectedTables;

/// <summary>Times are stored in UTC (SYSUTCDATETIME) but read back without a kind; marking them UTC makes the API send them
/// with a 'Z', so browsers show them in the viewer's own time zone instead of taking UTC for local time.</summary>
public static class ConnectedTime
{
    public static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
    public static DateTime? Utc(DateTime? value) => value is { } v ? Utc(v) : null;
}

// Results returned by the module's handlers (serialized as the "data" of the API responses). Internal BIGINT ids are never
// included; fields are identified by their FID (the Quickbase-compatible exception) and everything else by PublicId.

public sealed record ConnectionResult(
    Guid PublicId, string Name, string AuthMode, string Realm, string? RealmName, Guid SourceAppPublicId, string SourceAppName,
    bool IsCrossTenant, string? TokenPrefix, bool HasAppToken, string Status, string? StatusReason,
    DateTime? LastVerifiedOn, DateTime? LastUsedOn, int TablesUsing, DateTime CreatedOn, string RowVersion);

public sealed record SourceTableResult(Guid PublicId, string Name, string? PluralLabel, string? Description, string? Icon);

public sealed record SourceFieldResult(
    int Fid, string Label, string TypeCode, bool IsSystem, bool IsEncrypted, bool Connectable, string? Reason,
    string? DestinationTypeCode, string? Conversion);

public sealed record SourceFieldsResult(Guid TablePublicId, string TableName, IReadOnlyList<SourceFieldResult> Fields, bool RestrictedRecords);

public sealed record ConnectedFieldResult(
    int? DestinationFid, Guid DestinationFieldPublicId, string DestinationLabel, string DestinationTypeCode,
    int SourceFid, string SourceFieldName, string SourceTypeCode, string Conversion, string State, bool IsRefreshKey);

public sealed record ConnectedRunResult(
    Guid PublicId, string TriggeredBy, string? TriggeredByName, string Status, bool IsIncremental, long RowsRead, long Inserted, long Updated,
    long Unchanged, long Reverted, long Deleted, long Skipped, long Errored, long? HeldDeleteCount, string? ErrorDetail,
    DateTime QueuedOn, DateTime? StartedOn, DateTime? CompletedOn);

public sealed record ConnectedRunIssueResult(long? SourceRecordId, int? SourceFid, string Outcome, string ReasonCode, string Message);

public sealed record ConnectedRoleResult(Guid PublicId, string Name, bool CanRefresh);

public sealed record ConnectedTableSummaryResult(
    Guid PublicId, Guid TablePublicId, string TableName, string? Icon, string SourceRealm, string SourceAppName, string SourceTableName,
    bool IsCrossTenant, string UpdateMode, string ScheduleDescription, DateTime? NextRunOn, string Status, string? AttentionReason,
    DateTime? LastSuccessfulRunOn, ConnectedRunResult? ActiveRun, ConnectedRunResult? LastRun, bool CanRefresh);

public sealed record ConnectedTablePermissionsResult(bool IsOwner, bool IsAppAdmin, bool CanEditSource, bool CanEditOptions, bool CanRefresh);

public sealed record ConnectedTableResult(
    Guid PublicId, Guid AppPublicId, Guid TablePublicId, string TableName, string? Icon,
    ConnectionResult? Connection, string OwnerName, string SourceRealm, string SourceAppName, Guid SourceTablePublicId, string SourceTableName,
    bool IsCrossTenant, IReadOnlyList<ConnectedFieldResult> Fields, int RefreshKeySourceFid, string RefreshKeyLabel, bool RefreshKeyIsAuto,
    string UpdateMode, bool FilterEnabled, FilterGroup? Filter, ConnectedSchedule? Schedule, string ScheduleDescription, string TimeZone,
    DateTime? NextRunOn, bool NotifyOnFailure, int MassDeleteGuardPct, string Status, string? AttentionReason, DateTime? LastSuccessfulRunOn,
    ConnectedRunResult? ActiveRun, ConnectedRunResult? LastRun, IReadOnlyList<ConnectedRoleResult> Roles,
    ConnectedTablePermissionsResult Permissions, DateTime CreatedOn, string RowVersion);

public sealed record ConnectedMarkersResult(IReadOnlyList<Guid> TablePublicIds, IReadOnlyList<Guid> FieldPublicIds, IReadOnlyList<ConnectedTableFidsResult> TableFields);

/// <summary>The FIDs of a connected table's connected fields (some screens identify fields by FID rather than PublicId).</summary>
public sealed record ConnectedTableFidsResult(Guid TablePublicId, IReadOnlyList<int> Fids);

public sealed record SharingPolicyResult(string OutboundPolicy, bool InboundEnabled);

public sealed record DataShareResult(
    Guid PublicId, string ProviderRealm, string ConsumerRealm, string Scope, IReadOnlyList<Guid> AppPublicIds, bool AllowEncryptedFields,
    bool AllowCrossRegion, string Status, DateTime RequestedOn, string? RequestNote, DateTime? DecidedOn, DateTime? ExpiresOn, string RowVersion);

public sealed record ConnectedLinkResult(
    Guid PublicId, string ConsumerRealm, string ConsumerTableName, string ProviderAppName, string ProviderTableName, string? RunAsUser,
    string Status, DateTime? LastRunOn, long? LastRowCount, DateTime CreatedOn);
