namespace PowerBase.Application.Users.Commands.InviteUser;

public record InviteUserCommand(string Email, Guid RolePublicId, string FrontendBaseUrl);
