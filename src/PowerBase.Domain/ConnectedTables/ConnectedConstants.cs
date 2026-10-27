namespace PowerBase.Domain.ConnectedTables;

/// <summary>How a connection proves who reads the source app.</summary>
public static class ConnectedAuthModes
{
    /// <summary>Same realm only: the connection owner reads with their own app permissions. Nothing secret is stored.</summary>
    public const string SameRealmUser = "same_realm_user";
    /// <summary>A PowerBase user token (required for another realm). Stored only as a SHA-256 hash.</summary>
    public const string UserToken = "user_token";
    public static readonly string[] All = [SameRealmUser, UserToken];
}

public static class ConnectedConnectionStatuses
{
    public const string Active = "active";
    public const string Revoked = "revoked";
    public const string Unavailable = "unavailable";
}

public static class ConnectedUpdateModes
{
    /// <summary>"Make my table match the source exactly": records no longer in the source are removed.</summary>
    public const string Mirror = "mirror";
    /// <summary>"Keep everything and add new records": nothing is removed by a refresh.</summary>
    public const string Append = "append";
    public static readonly string[] All = [Mirror, Append];
}

public static class ConnectedTableStatuses
{
    public const string Active = "active";
    public const string Paused = "paused";
    public const string NeedsAttention = "needs_attention";
    /// <summary>A mirror refresh was stopped by the mass-delete guard and waits for the owner to confirm.</summary>
    public const string Held = "held";
    /// <summary>The destination table was deleted outside the module.</summary>
    public const string Removed = "removed";
}

public static class ConnectedRunStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Success = "success";
    public const string Partial = "partial";
    public const string Held = "held";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static bool IsFinished(string status) => status is Success or Partial or Held or Failed or Cancelled;
}

public static class ConnectedRunTriggers
{
    public const string Manual = "manual";
    public const string Schedule = "schedule";
    public const string Api = "api";
    public const string Retry = "retry";
}

public static class ConnectedFieldStates
{
    public const string Connected = "connected";
    public const string SourceMissing = "source_missing";
    public const string TypeConflict = "type_conflict";
}

/// <summary>How a source value becomes the destination value.</summary>
public static class ConnectedConversions
{
    public const string Identity = "identity";
    /// <summary>A computed source field (formula / lookup / summary): its resolved value is stored.</summary>
    public const string Computed = "computed";
    /// <summary>User id(s) from another realm stored as email text.</summary>
    public const string UserToEmail = "user_to_email";
    /// <summary>A reference stores the parent's source Record ID# as a number.</summary>
    public const string ReferenceToNumber = "reference_to_number";
    /// <summary>A value with no matching destination type stored as text.</summary>
    public const string ToText = "to_text";
}

public static class ConnectedIssueOutcomes
{
    public const string Skipped = "skipped";
    public const string Errored = "errored";
    public const string Warning = "warning";
}

public static class ConnectedDataShareStatuses
{
    public const string Requested = "requested";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Revoked = "revoked";
}

public static class ConnectedDataShareScopes
{
    public const string AllApps = "all_apps";
    public const string Apps = "apps";
}

public static class ConnectedLinkStatuses
{
    public const string Active = "active";
    public const string RevokedByProvider = "revoked_by_provider";
    public const string Removed = "removed";
}

public static class ConnectedOutboundPolicies
{
    public const string Disabled = "disabled";
    public const string ApprovedOnly = "approved_only";
}

/// <summary>Error codes returned by the module's endpoints and the read-only write filter.</summary>
public static class ConnectedErrorCodes
{
    public const string ReadOnly = "CONNECTED_TABLE_READ_ONLY";
    public const string Forbidden = "CONNECTED_TABLE_FORBIDDEN";
    public const string RefreshInProgress = "CONNECTED_REFRESH_IN_PROGRESS";
    public const string DataShareNotApproved = "DATA_SHARE_NOT_APPROVED";
    public const string SourceAppDisallows = "SOURCE_APP_DISALLOWS_CONNECTIONS";
    public const string InboundDisabled = "INBOUND_CONNECTIONS_DISABLED";
    public const string CrossRegion = "CROSS_REGION_NOT_ALLOWED";
    public const string RefreshKeyNotUnique = "REFRESH_KEY_NOT_UNIQUE";
    public const string ConnectionInUse = "CONNECTION_IN_USE";
    public const string ConnectionUnusable = "CONNECTION_UNUSABLE";
    public const string SourceUnavailable = "CONNECTED_SOURCE_UNAVAILABLE";
    public const string LimitExceeded = "CONNECTED_LIMIT_EXCEEDED";
}

/// <summary>Source field ids PowerBase reserves for built-in fields (Quickbase convention).</summary>
public static class ConnectedSystemFids
{
    public const int DateCreated = 1;
    public const int DateModified = 2;
    public const int RecordId = 3;
    public const int RecordOwner = 4;
    public const int LastModifiedBy = 5;
}
