namespace PowerBase.Application.Roles.Commands.CreateRole;

public record CreateRoleCommand(string Name, string? Description, IReadOnlyList<string> PermissionCodes);
