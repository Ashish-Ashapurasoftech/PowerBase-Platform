using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Auth.Queries.Login;

public class LoginResult
{
    public string Token { get; init; } = string.Empty;
    public DateTime ExpiresAt { get; init; }
    public Guid UserPublicId { get; init; }
    public string Email { get; init; } = string.Empty;
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
}

public class LoginQueryHandler
{
    private readonly IUserRepository _userRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly IJwtService _jwtService;
    private readonly IPasswordService _passwordService;
    private readonly ITenantRepository _tenantRepo;
    private readonly IQueryContext _queryContext;

    public LoginQueryHandler(
        IUserRepository userRepo,
        ITenantRepository tenantRepo,
        IAuditRepository auditRepo,
        IJwtService jwtService,
        IPasswordService passwordService,
        IQueryContext queryContext)
    {
        _userRepo = userRepo;
        _tenantRepo = tenantRepo;
        _auditRepo = auditRepo;
        _jwtService = jwtService;
        _passwordService = passwordService;
        _queryContext = queryContext;
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
        if (user is null || !_passwordService.Verify(query.Password, user.PasswordHash))
        {
            await _auditRepo.RecordLoginAttemptAsync(query.Email, query.IpAddress, isSuccess: false, ct);
            throw new UnauthorizedActionException("login");
        }

        await _auditRepo.RecordLoginAttemptAsync(query.Email, query.IpAddress, isSuccess: true, ct);

        var tenantId = await _tenantRepo.GetActiveTenantIdByUserIdAsync(user.Id, ct);

        var token = _jwtService.GenerateToken(user, tenantId, out var jwtId);
        var expiresAt = DateTime.UtcNow.AddDays(1);

        await _auditRepo.CreateSessionAsync(user.Id, tenantId, jwtId, query.IpAddress, expiresAt, ct);

        return new LoginResult
        {
            Token = token,
            ExpiresAt = expiresAt,
            UserPublicId = user.PublicId,
            Email = user.Email,
            FirstName = user.FirstName,
            LastName = user.LastName,
        };
    }
}
