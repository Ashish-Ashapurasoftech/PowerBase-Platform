namespace PowerBase.Application.Auth.Commands.AcceptInvite;

public record AcceptInviteCommand(string Token, string Name, string Password);
