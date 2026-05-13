namespace PowerBase.API.Models.Auth;

public class SignupRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;
}
