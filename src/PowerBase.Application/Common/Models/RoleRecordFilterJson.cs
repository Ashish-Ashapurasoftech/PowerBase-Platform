using System.Text.Json;
using PowerBase.Application.Reports;

namespace PowerBase.Application.Common.Models;

/// <summary>
/// Reads/writes meta.AppRoleRecordFilter.FilterJson, which now holds one of two shapes:
///   • legacy  — a JSON array of <see cref="RoleRecordFilterCondition"/> (flat, field by PublicId);
///   • group   — a JSON object <see cref="FilterGroup"/> (nested ALL/ANY tree, field by numeric Fid —
///               the same model reports use, so every report operator and value tier is expressible).
/// Rows are never migrated: a legacy row keeps working until its role/table filter is next saved.
/// </summary>
public static class RoleRecordFilterJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>True when the stored JSON is the nested <see cref="FilterGroup"/> object shape.</summary>
    public static bool IsGroupFormat(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        foreach (var c in json)
        {
            if (char.IsWhiteSpace(c)) continue;
            return c == '{';
        }
        return false;
    }

    /// <summary>Parses the group shape; null when the JSON is legacy, empty or malformed.</summary>
    public static FilterGroup? ParseGroup(string? json)
    {
        if (!IsGroupFormat(json)) return null;
        try { return JsonSerializer.Deserialize<FilterGroup>(json!, Options); }
        catch (JsonException) { return null; }
    }

    public static string SerializeGroup(FilterGroup group) => JsonSerializer.Serialize(group, Options);

    /// <summary>Parses the legacy flat shape; empty when the JSON is the group shape or malformed.</summary>
    public static List<RoleRecordFilterCondition> ParseLegacyConditions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || IsGroupFormat(json)) return new();
        try { return JsonSerializer.Deserialize<List<RoleRecordFilterCondition>>(json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    /// <summary>Number of leaf conditions in the tree (0 = the filter restricts nothing).</summary>
    public static int CountConditions(FilterGroup? group)
    {
        if (group is null) return 0;
        var count = 0;
        foreach (var node in group.Nodes)
        {
            if (node.Condition is not null) count++;
            if (node.Group is not null) count += CountConditions(node.Group);
        }
        return count;
    }
}
