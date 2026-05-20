namespace PowerBase.Application.Apps.Commands.AddAppUser;

public record AddAppUserCommand(Guid AppPublicId, string Email, Guid? RolePublicId);
