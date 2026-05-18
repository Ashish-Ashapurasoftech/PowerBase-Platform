namespace PowerBase.API.Models.Auth;

public class AuthResponse
{
    public string Token { get; init; } = string.Empty;
    public string ExpiresAt { get; init; } = string.Empty;
    public UserResponse User { get; init; } = new();
    public Guid TenantPublicId { get; init; }
    public string TenantName { get; init; } = string.Empty;
}
