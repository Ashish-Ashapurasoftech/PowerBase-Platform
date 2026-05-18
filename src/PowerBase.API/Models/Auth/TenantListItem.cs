namespace PowerBase.API.Models.Auth;

public class TenantListItem
{
    public Guid PublicId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public bool IsOwner { get; init; }
}
