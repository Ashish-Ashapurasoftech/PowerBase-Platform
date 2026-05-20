namespace PowerBase.Application.Roles.Commands.UpdateRole;

public record UpdateRoleCommand(Guid PublicId, string Name, string? Description);
