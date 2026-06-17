namespace PowerBase.Application.Apps.Commands.InviteAppUser;

public record InviteAppUserCommand(
    Guid AppPublicId,
    string Email,
    Guid? AppRolePublicId,
    string FrontendBaseUrl);
