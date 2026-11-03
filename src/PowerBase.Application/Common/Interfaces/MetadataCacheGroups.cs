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
}
