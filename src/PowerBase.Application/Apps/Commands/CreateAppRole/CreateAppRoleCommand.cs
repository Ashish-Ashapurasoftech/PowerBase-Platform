namespace PowerBase.Application.Apps.Commands.CreateAppRole;

public record CreateAppRoleCommand(Guid AppPublicId, string Name, bool IsDefault);
