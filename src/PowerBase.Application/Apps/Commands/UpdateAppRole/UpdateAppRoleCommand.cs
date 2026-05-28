namespace PowerBase.Application.Apps.Commands.UpdateAppRole;

public record UpdateAppRoleCommand(
    Guid AppPublicId,
    Guid RolePublicId,
    IReadOnlyList<string> Permissions);
