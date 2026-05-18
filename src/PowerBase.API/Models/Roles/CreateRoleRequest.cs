namespace PowerBase.API.Models.Roles;

public class CreateRoleRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<string> PermissionCodes { get; set; } = [];
}
