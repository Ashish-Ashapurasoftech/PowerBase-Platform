namespace PowerBase.Application.Auth.Commands.AcceptInvite;

public record AcceptInviteCommand(string Token, string? FirstName, string? LastName, string? Name, string Password);
