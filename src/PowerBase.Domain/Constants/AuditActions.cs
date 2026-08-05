namespace PowerBase.Domain.Constants;

public static class AuditActions
{
    public const string Created = "Created";
    public const string Updated = "Updated";
    public const string Deleted = "Deleted";
    public const string SchemaChanged = "SchemaChanged";
    public const string RoleChanged = "RoleChanged";
    public const string PermissionChanged = "PermissionChanged";
    public const string InviteSent = "InviteSent";
    public const string InviteAccepted = "InviteAccepted";
    public const string LoginSucceeded = "LoginSucceeded";
    public const string LoginFailed = "LoginFailed";
    /// <summary>A record write performed by an Action Button click, including any
    /// privileged-write fields the invoking user could not otherwise edit directly.</summary>
    public const string ButtonInvoked = "ButtonInvoked";

    /// <summary>A Page was published or unpublished.</summary>
    public const string Published = "Published";
    /// <summary>A Page was restored to an earlier version.</summary>
    public const string VersionRestored = "VersionRestored";
    /// <summary>A Page was rendered/opened by a viewer — logged as data access, not a schema change.</summary>
    public const string Viewed = "Viewed";
}
