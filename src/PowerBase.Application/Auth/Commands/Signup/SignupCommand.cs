namespace PowerBase.Application.Auth.Commands.Signup;

public record SignupCommand(
    string Email,
    string Password,
    string FirstName,
    string LastName,
    string TenantName
);
