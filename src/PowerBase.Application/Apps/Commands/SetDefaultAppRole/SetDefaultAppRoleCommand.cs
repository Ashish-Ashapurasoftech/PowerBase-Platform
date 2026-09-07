namespace PowerBase.Application.Apps.Commands.SetDefaultAppRole;

public record SetDefaultAppRoleCommand(Guid AppPublicId, Guid RolePublicId);
