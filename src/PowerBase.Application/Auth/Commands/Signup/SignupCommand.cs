namespace PowerBase.Application.Auth.Commands.Signup;

public record SignupCommand(string Email, string Password, string Name, string? FirstName = null, string? LastName = null);
