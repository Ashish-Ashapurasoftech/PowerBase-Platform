namespace PowerBase.Application.Auth.Queries.Login;

public record LoginQuery(string Email, string Password, string IpAddress);
