using PowerBase.Application.ConnectedTables.Scheduling;
using PowerBase.Application.Reports;

namespace PowerBase.API.ConnectedTables.Models;

public sealed class CreateConnectionRequest
{
    public string Name { get; set; } = string.Empty;
    /// <summary>same_realm_user | user_token</summary>
    public string AuthMode { get; set; } = "user_token";
    /// <summary>The source realm's PowerBase URL (or just its name). Not needed for same_realm_user.</summary>
    public string? Realm { get; set; }
    /// <summary>The source app's Application ID (its PublicId).</summary>
    public Guid AppId { get; set; }
    public string? UserToken { get; set; }
    public string? AppToken { get; set; }
}

public sealed class UpdateConnectionRequest
{
    public string Name { get; set; } = string.Empty;
    public string? UserToken { get; set; }
    public string? AppToken { get; set; }
    public bool ClearAppToken { get; set; }
    public string RowVersion { get; set; } = string.Empty;
}

public sealed class RowVersionRequest
{
    public string RowVersion { get; set; } = string.Empty;
}

public sealed class PreviewCountRequest
{
    public FilterGroup? Filter { get; set; }
}

public sealed class CreateConnectedTableRequest
{
    public Guid AppId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? SingularLabel { get; set; }
    public string? PluralLabel { get; set; }
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid SourceTableId { get; set; }
    /// <summary>The source FIDs to connect, in the order the fields should appear.</summary>
    public List<int> SourceFids { get; set; } = new();
    /// <summary>Defaults to the source Record ID# (3).</summary>
    public int? RefreshKeyFid { get; set; }
    /// <summary>mirror | append</summary>
    public string UpdateMode { get; set; } = "mirror";
    public bool FilterEnabled { get; set; }
    public FilterGroup? Filter { get; set; }
    public ConnectedSchedule? Schedule { get; set; }
    public string? TimeZone { get; set; }
    public bool NotifyOnFailure { get; set; } = true;
}

public sealed class UpdateFilterRequest
{
    public bool FilterEnabled { get; set; }
    public FilterGroup? Filter { get; set; }
    public string RowVersion { get; set; } = string.Empty;
}

public sealed class ConnectFieldsRequest
{
    public List<int> SourceFids { get; set; } = new();
    public string RowVersion { get; set; } = string.Empty;
}

public sealed class DisconnectFieldRequest
{
    public Guid FieldId { get; set; }
    /// <summary>Also delete the destination field (otherwise it stays as an ordinary, editable field).</summary>
    public bool DeleteField { get; set; }
    public string RowVersion { get; set; } = string.Empty;
}

public sealed class UpdateRefreshOptionsRequest
{
    public string UpdateMode { get; set; } = "mirror";
    public ConnectedSchedule? Schedule { get; set; }
    public string? TimeZone { get; set; }
    public bool NotifyOnFailure { get; set; } = true;
    public int MassDeleteGuardPct { get; set; } = 50;
    /// <summary>Required when switching from "keep everything" to "match the source exactly".</summary>
    public bool ConfirmModeChange { get; set; }
    public string RowVersion { get; set; } = string.Empty;
}

public sealed class ChangeRefreshKeyRequest
{
    public int SourceFid { get; set; }
    public bool Confirm { get; set; }
    public string RowVersion { get; set; } = string.Empty;
}

public sealed class SwitchConnectionRequest
{
    public Guid ConnectionId { get; set; }
    public string RowVersion { get; set; } = string.Empty;
}

public sealed class RefreshPermissionRequest
{
    public List<Guid> RoleIds { get; set; } = new();
}

public sealed class SharingPolicyRequest
{
    /// <summary>disabled | approved_only</summary>
    public string OutboundPolicy { get; set; } = "approved_only";
    public bool InboundEnabled { get; set; } = true;
}

public sealed class DataShareRequestRequest
{
    public string Realm { get; set; } = string.Empty;
    public string? Note { get; set; }
}

public sealed class ApproveDataShareRequest
{
    /// <summary>all_apps | apps</summary>
    public string Scope { get; set; } = "all_apps";
    public List<Guid>? AppIds { get; set; }
    public bool AllowEncryptedFields { get; set; }
    public bool AllowCrossRegion { get; set; }
    public DateTime? ExpiresOn { get; set; }
    public string RowVersion { get; set; } = string.Empty;
}
