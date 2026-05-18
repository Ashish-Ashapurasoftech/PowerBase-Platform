namespace PowerBase.Application.Apps.Commands.ChangeAppUserRole;

public record ChangeAppUserRoleCommand(Guid AppPublicId, Guid UserPublicId, Guid RolePublicId);
