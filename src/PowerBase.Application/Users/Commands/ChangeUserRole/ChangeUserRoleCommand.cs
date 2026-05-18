namespace PowerBase.Application.Users.Commands.ChangeUserRole;

public record ChangeUserRoleCommand(Guid UserPublicId, Guid RolePublicId);
