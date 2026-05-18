namespace PowerBase.Application.Roles.Commands.UpdateRolePermissions;

public record UpdateRolePermissionsCommand(Guid RolePublicId, IReadOnlyList<string> PermissionCodes);
