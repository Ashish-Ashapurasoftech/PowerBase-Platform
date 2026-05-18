namespace PowerBase.API.Models.Users;

public class TenantUserResponse
{
    public Guid UserPublicId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Guid? RolePublicId { get; set; }
    public string? RoleName { get; set; }
    public bool IsOwner { get; set; }
    public bool IsActive { get; set; }
    public DateTime JoinedOn { get; set; }
}
