namespace PowerBase.API.Models.Auth;

public class IdentityResponse
{
    public string IdentityToken { get; init; } = string.Empty;
    public string ExpiresAt { get; init; } = string.Empty;
    public UserResponse User { get; init; } = new();
    public IReadOnlyList<TenantListItem> Tenants { get; init; } = [];
}
