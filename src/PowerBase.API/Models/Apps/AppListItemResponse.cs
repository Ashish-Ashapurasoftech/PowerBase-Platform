namespace PowerBase.API.Models.Apps;

/// <summary>
/// Slim shape for the apps listing (GET /apps, GET /apps/search). Deliberately omits Formatting and
/// SecurityOptions (the latter carries the wrapped DEK) — those are only returned by GET /apps/{id}.
/// </summary>
public class AppListItemResponse
{
    public Guid PublicId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? Icon { get; init; }
    public string? Color { get; init; }
    public string Status { get; init; } = string.Empty;
    public DateTime CreatedOn { get; init; }
    public string? OwnerName { get; init; }
    public bool IsEncrypted { get; init; }
}
