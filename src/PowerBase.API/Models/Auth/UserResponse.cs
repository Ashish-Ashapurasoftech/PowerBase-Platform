namespace PowerBase.API.Models.Auth;

public class UserResponse
{
    public Guid PublicId { get; init; }
    public string Email { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}
