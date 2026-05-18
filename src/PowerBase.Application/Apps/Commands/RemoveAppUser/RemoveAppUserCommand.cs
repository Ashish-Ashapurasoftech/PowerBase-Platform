namespace PowerBase.Application.Apps.Commands.RemoveAppUser;

public record RemoveAppUserCommand(Guid AppPublicId, Guid UserPublicId);
