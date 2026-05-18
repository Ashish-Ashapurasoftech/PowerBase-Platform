namespace PowerBase.API.Models.Users;

public class InviteUserRequest
{
    public string Email { get; set; } = string.Empty;
    public Guid RolePublicId { get; set; }
}
