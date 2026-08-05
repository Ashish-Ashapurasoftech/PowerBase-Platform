namespace PowerBase.Application.Groups.Queries.GetUserEffectivePermissions;

public class UserEffectivePermissionsDto
{
    public Guid UserPublicId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string UserEmail { get; set; } = string.Empty;
    public List<AppPermissionDetailDto> Apps { get; set; } = new();
}

public class AppPermissionDetailDto
{
    public Guid AppPublicId { get; set; }
    public string AppName { get; set; } = string.Empty;
    public string? DirectRoleName { get; set; }
    public List<InheritedRoleDto> InheritedRoles { get; set; } = new();
    public List<string> ConsolidatedPermissions { get; set; } = new();
}

public class InheritedRoleDto
{
    public Guid GroupPublicId { get; set; }
    public string GroupName { get; set; } = string.Empty;
    public Guid AppRolePublicId { get; set; }
    public string AppRoleName { get; set; } = string.Empty;
}
