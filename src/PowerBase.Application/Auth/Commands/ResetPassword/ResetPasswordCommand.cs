namespace PowerBase.Application.Auth.Commands.ResetPassword;

public record ResetPasswordCommand(string Token, string NewPassword);
