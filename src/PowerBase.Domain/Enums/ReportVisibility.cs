namespace PowerBase.Domain.Enums;

/// <summary>
/// Report-only visibility values. <see cref="Visibility"/> is shared with pages (which have a DB
/// CHECK constraint), so report-specific values live here instead of in that enum.
/// </summary>
public static class ReportVisibility
{
    /// <summary>Not listed in any panel/list; reachable only via direct URL/bookmark.</summary>
    public const string Hidden = "Hidden";

    public static readonly IReadOnlyList<string> All =
        [.. Enum.GetNames<Visibility>(), Hidden];

    public static bool IsValid(string? value) => value is not null && All.Contains(value);
}
