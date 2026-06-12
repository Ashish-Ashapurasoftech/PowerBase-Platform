using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Auth.Commands.ResetPassword;

public class ResetPasswordCommandHandler
{
    private readonly IUserRepository _userRepo;
    private readonly IJwtService _jwtService;
    private readonly IPasswordService _passwordService;
    private readonly IUnitOfWork _uow;

    public ResetPasswordCommandHandler(
        IUserRepository userRepo,
        IJwtService jwtService,
        IPasswordService passwordService,
        IUnitOfWork uow)
    {
        _userRepo = userRepo;
        _jwtService = jwtService;
        _passwordService = passwordService;
        _uow = uow;
    }

    public async Task HandleAsync(ResetPasswordCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.Token))
            throw new ValidationException(new Dictionary<string, string[]> { ["token"] = ["Reset token is required."] });
            
        if (string.IsNullOrWhiteSpace(command.NewPassword) || command.NewPassword.Length < 8)
            throw new ValidationException(new Dictionary<string, string[]> { ["password"] = ["Password must be at least 8 characters."] });

        if (!_jwtService.ValidatePasswordResetToken(command.Token, out var userId, out var tokenHash))
            throw new UnauthorizedActionException("Invalid or expired password reset token.");

        var user = await _userRepo.GetByIdAsync(userId, ct);
        if (user == null || !user.IsActive || user.IsDeleted)
            throw new UnauthorizedActionException("User account is inactive or disabled.");

        // Ensure the token was generated for the current password hash
        if (user.HashedPassword != tokenHash)
            throw new UnauthorizedActionException("Password reset token is no longer valid because the password was already changed.");

        user.HashedPassword = _passwordService.Hash(command.NewPassword);
        user.ModifiedOn = DateTime.UtcNow;

        await _userRepo.UpdatePasswordAsync(user.Id, user.HashedPassword, ct);
        // Note: UpdatePasswordAsync handles DB update directly, no need for uow.CommitAsync for this repo method if it's auto-commit.
    }
}
