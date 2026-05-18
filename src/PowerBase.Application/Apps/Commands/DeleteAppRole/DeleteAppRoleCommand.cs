namespace PowerBase.Application.Apps.Commands.DeleteAppRole;

public record DeleteAppRoleCommand(Guid AppPublicId, Guid RolePublicId);
