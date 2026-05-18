namespace PowerBase.API.Models.Roles;

public class PermissionResponse
{
    public string Code { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
}
