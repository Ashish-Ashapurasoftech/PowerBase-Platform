namespace PowerBase.Application.Common.Interfaces;

/// <summary>The groups of <see cref="IMetadataCache"/>. A repository drops a group BEFORE every write that changes what its cached reads return.</summary>
public static class MetadataCacheGroups
{
    public const string Fields = "fields";
    public const string Tables = "tables";
    public const string Apps = "apps";
    public const string Relationships = "relationships";

    /// <summary>Everything that depends on who the user is or what their roles allow: role assignments, role and table permissions,
    /// field access, record rules, report visibility. One group, so a change to any of them clears all of them.</summary>
    public const string Access = "access";

    /// <summary>The app id behind a table, report or app public id. A public id never moves to another app, so only a delete changes it.</summary>
    public const string Ids = "ids";

    /// <summary>A user's permission codes in a tenant (control database). Cached under <see cref="GlobalTenant"/>, because a role edit
    /// there does not say which tenants it reaches.</summary>
    public const string UserPermissions = "userperms";

    /// <summary>"This session is not revoked" answers (control database), so a logout on any instance is honoured by every instance on
    /// its next request. Cached under <see cref="GlobalTenant"/>.</summary>
    public const string Sessions = "sessions";

    /// <summary>Forms, their layouts and form rules of a tenant (also read by grid edit). Dropped by form, layout, rule and field writes.</summary>
    public const string Forms = "forms";

    /// <summary>App variables of a tenant.</summary>
    public const string Variables = "variables";

    /// <summary>Which tables of a tenant are connected (read-only write filter). Dropped when a connected table is created or removed.</summary>
    public const string ConnectedTables = "connected";

    /// <summary>A user's sign-in identity (control database: active, deleted, system role, name, email — never the password hash),
    /// and the immutable id → public id map. Cached under <see cref="GlobalTenant"/>.</summary>
    public const string Users = "users";

    /// <summary>Tenant rows (control database: status, plan, Code Pages switch). Cached under <see cref="GlobalTenant"/>.</summary>
    public const string Tenants = "tenants";

    /// <summary>API user tokens (pb_ut_…) by hash and their allowed apps (control database). Cached under <see cref="GlobalTenant"/>;
    /// dropped by every token write except the last-used stamp.</summary>
    public const string UserTokens = "usertokens";

    /// <summary>The field type catalogue (core.FieldType in each tenant database): seeded reference data, changed only by migrations.
    /// One entry per tenant; the TTL is the backstop.</summary>
    public const string FieldTypes = "fieldtypes";

    /// <summary>Groups that decide what a user may do or see: never served from the cache unless their shared version was confirmed
    /// (fail closed — see <see cref="ICacheVersionStore"/>).</summary>
    public static bool IsSecurityRelevant(string group) => group is Access or UserPermissions or Sessions or Users or Tenants or UserTokens or ConnectedTables;

    /// <summary>The tenant id for entries that are not tenant-bound.</summary>
    public const long GlobalTenant = 0;
}
