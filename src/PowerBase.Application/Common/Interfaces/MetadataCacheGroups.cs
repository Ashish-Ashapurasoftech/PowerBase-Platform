namespace PowerBase.Application.Common.Interfaces;

/// <summary>The groups of <see cref="IMetadataCache"/>. A repository drops a group BEFORE every write that changes what its cached reads return.</summary>
public static class MetadataCacheGroups
{
    public const string Fields = "fields";
    public const string Tables = "tables";
    public const string Apps = "apps";

    /// <summary>Everything that depends on who the user is or what their roles allow: role assignments, role and table permissions,
    /// field access, record rules, report visibility. One group, so a change to any of them clears all of them.</summary>
    public const string Access = "access";

    /// <summary>The app id behind a table, report or app public id. A public id never moves to another app, so only a delete changes it.</summary>
    public const string Ids = "ids";

    /// <summary>A user's permission codes in a tenant (control database). Cached under <see cref="GlobalTenant"/>, because a role edit
    /// there does not say which tenants it reaches.</summary>
    public const string UserPermissions = "userperms";

    /// <summary>The tenant id for entries that are not tenant-bound.</summary>
    public const long GlobalTenant = 0;
}
