namespace PowerBase.API.Models.Apps;

public class AddAppUserRequest
{
    public string Email { get; init; } = string.Empty;
    public Guid RolePublicId { get; init; }
}
