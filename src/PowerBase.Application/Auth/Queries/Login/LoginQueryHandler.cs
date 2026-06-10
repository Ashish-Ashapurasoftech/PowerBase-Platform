using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Tenants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Auth.Queries.Login;

public class LoginResult
{
    public string IdentityToken { get; init; } = string.Empty;
    public DateTime ExpiresAt { get; init; }
    public Guid UserPublicId { get; init; }
    public string Email { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<TenantItem> Tenants { get; init; } = [];
}

public class LoginQueryHandler
{
    private readonly IUserRepository _userRepo;
    private readonly ITenantRepository _tenantRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly IJwtService _jwtService;
    private readonly IPasswordService _passwordService;

    public LoginQueryHandler(
        IUserRepository userRepo,
        ITenantRepository tenantRepo,
        IAuditRepository auditRepo,
        IJwtService jwtService,
        IPasswordService passwordService)
    {
        _userRepo = userRepo;
        _tenantRepo = tenantRepo;
        _auditRepo = auditRepo;
        _jwtService = jwtService;
        _passwordService = passwordService;
    }

    public async Task<LoginResult> HandleAsync(LoginQuery query, CancellationToken ct = default)
    {
        var validator = new LoginQueryValidator();
        var validation = await validator.ValidateAsync(query, ct);
        if (!validation.IsValid)
            throw new ValidationException(
                validation.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        var user = await _userRepo.GetByEmailAsync(query.Email, ct);
        if (user is null || !_passwordService.Verify(query.Password, user.HashedPassword))
        {
            await _auditRepo.RecordLoginAttemptAsync(query.Email, query.IpAddress, wasSuccessful: false,
                userId: user?.Id, failureReason: "Invalid credentials", ct);
            throw new BadRequestException("INVALID_CREDENTIALS", "Invalid email or password.");
        }

        await _auditRepo.RecordLoginAttemptAsync(query.Email, query.IpAddress, wasSuccessful: true, userId: user.Id, ct: ct);

        var tenants = await _tenantRepo.ListTenantsForUserAsync(user.Id, ct);
        var identityToken = _jwtService.GenerateIdentityToken(user, out _, out var expiresAt);

        return new LoginResult
        {
            IdentityToken = identityToken,
            ExpiresAt = expiresAt,
            UserPublicId = user.PublicId,
            Email = user.Email,
            Name = user.Name,
            Tenants = tenants,
        };
    }
}
