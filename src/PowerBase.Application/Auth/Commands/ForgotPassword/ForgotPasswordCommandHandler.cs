using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Auth.Commands.ForgotPassword;

public class ForgotPasswordCommandHandler
{
    private readonly IUserRepository _userRepo;
    private readonly IJwtService _jwtService;
    private readonly IEmailService _emailService;

    public ForgotPasswordCommandHandler(
        IUserRepository userRepo,
        IJwtService jwtService,
        IEmailService emailService)
    {
        _userRepo = userRepo;
        _jwtService = jwtService;
        _emailService = emailService;
    }

    public async Task HandleAsync(ForgotPasswordCommand command, string appBaseUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.Email))
            throw new ValidationException(new Dictionary<string, string[]> { ["email"] = ["Email is required."] });

        var user = await _userRepo.GetByEmailAsync(command.Email, ct);
        if (user == null || !user.IsActive || user.IsDeleted)
        {
            // Do not reveal if the user exists or not for security reasons, just return silently.
            return;
        }

        var resetToken = _jwtService.GeneratePasswordResetToken(user);
        
        // Ensure base URL ends with a slash before appending the path
        var baseUrl = appBaseUrl.TrimEnd('/');
        var resetLink = $"{baseUrl}/auth/reset-password?token={resetToken}";

        await _emailService.SendPasswordResetEmailAsync(user.Email, resetLink, ct);
    }
}
