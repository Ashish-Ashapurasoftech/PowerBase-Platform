using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Auth.Commands.Signup;

public class SignupCommandResult
{
    public string IdentityToken { get; init; } = string.Empty;
    public DateTime ExpiresAt { get; init; }
    public Guid UserPublicId { get; init; }
    public string Email { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
}

public class SignupCommandHandler
{
    private readonly IUserRepository _userRepo;
    private readonly IJwtService _jwtService;
    private readonly IPasswordService _passwordService;

    public SignupCommandHandler(IUserRepository userRepo, IJwtService jwtService, IPasswordService passwordService)
    {
        _userRepo = userRepo;
        _jwtService = jwtService;
        _passwordService = passwordService;
    }

    public async Task<SignupCommandResult> HandleAsync(SignupCommand command, CancellationToken ct = default)
    {
        var validator = new SignupCommandValidator();
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(
                validation.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        if (await _userRepo.GetByEmailAsync(command.Email, ct) is not null)
            throw new DuplicateException("User", "email", command.Email);

        var firstName = !string.IsNullOrWhiteSpace(command.FirstName)
            ? command.FirstName.Trim()
            : (command.Name?.Split(' ').FirstOrDefault() ?? string.Empty);
        var lastName = !string.IsNullOrWhiteSpace(command.LastName)
            ? command.LastName.Trim()
            : (command.Name != null && command.Name.Contains(' ') ? command.Name[(command.Name.IndexOf(' ') + 1)..].Trim() : string.Empty);
        var name = string.IsNullOrWhiteSpace(command.Name) ? $"{firstName} {lastName}".Trim() : command.Name.Trim();

        var hashedPassword = _passwordService.Hash(command.Password);
        var userId = await _userRepo.CreateAsync(new User
        {
            Email = command.Email,
            HashedPassword = hashedPassword,
            Name = name,
            FirstName = firstName,
            LastName = lastName,
            IsActive = true,
        }, ct: ct);

        var created = await _userRepo.GetByIdAsync(userId, ct);
        var identityToken = _jwtService.GenerateIdentityToken(created, out _, out var expiresAt);

        return new SignupCommandResult
        {
            IdentityToken = identityToken,
            ExpiresAt = expiresAt,
            UserPublicId = created.PublicId,
            Email = created.Email,
            Name = created.Name,
            FirstName = created.FirstName,
            LastName = created.LastName,
        };
    }
}
