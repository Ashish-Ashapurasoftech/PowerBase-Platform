namespace PowerBase.API.QuickbaseApi.V1.Models;

/// <summary>One item of GET /v1/tables (Quickbase getAppTables). Every documented field is always present; null when unknown.</summary>
public sealed class QbTableResponse
{
    public string Name { get; init; } = "";
    public string Id { get; init; } = "";
    public string? Alias { get; init; }
    public string? Description { get; init; }
    public string? Created { get; init; }
    public string? Updated { get; init; }
    public long? NextRecordId { get; init; }
    public long? NextFieldId { get; init; }
    public long? DefaultSortFieldId { get; init; }
    public string? DefaultSortOrder { get; init; }
    public long? KeyFieldId { get; init; }
    public string? SingleRecordName { get; init; }
    public string? PluralRecordName { get; init; }
    public string? SizeLimit { get; init; }
    public string? SpaceUsed { get; init; }
    public string? SpaceRemaining { get; init; }
}

/// <summary>One item of GET /v1/fields (Quickbase getFields).</summary>
public sealed class QbFieldResponse
{
    public long Id { get; init; }
    public string FieldType { get; init; } = "";
    public string Mode { get; init; } = "";
    public string Label { get; init; } = "";
    public bool? NoWrap { get; init; }
    public bool? Bold { get; init; }
    public bool? Required { get; init; }
    public bool? AppearsByDefault { get; init; }
    public bool? FindEnabled { get; init; }
    public bool? Unique { get; init; }
    public bool? DoesDataCopy { get; init; }
    public string? FieldHelp { get; init; }
    public bool? Audited { get; init; }
    /// <summary>All 73 documented property keys, in schema order; null where we have no equivalent.</summary>
    public IDictionary<string, object?> Properties { get; init; } = new Dictionary<string, object?>();
    /// <summary>Present when includeFieldPerms=true (null otherwise).</summary>
    /// <summary>Only with includeFieldPerms=true (Quickbase leaves the key out otherwise).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<QbFieldPermission>? Permissions { get; init; }
}

public sealed record QbFieldPermission(string Role, string PermissionType, string RoleId);
