namespace PowerBase.API.Models.Auth;

public record AcceptInviteRequest(string Token, string? Name, string? FirstName, string? LastName, string Password);
