using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Auth.Commands.RefreshToken;

public class RefreshTokenResult
{
    public string Token { get; init; } = string.Empty;
    public DateTime ExpiresAt { get; init; }
    public Guid UserPublicId { get; init; }
    public string Email { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public Guid TenantPublicId { get; init; }
    public string TenantName { get; init; } = string.Empty;
}

public class RefreshTokenCommandHandler
{
    private readonly ITenantRepository _tenantRepo;
    private readonly IUserRepository _userRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly IJwtService _jwtService;
    private readonly IQueryContext _queryContext;

    public RefreshTokenCommandHandler(
        ITenantRepository tenantRepo,
        IUserRepository userRepo,
        IAuditRepository auditRepo,
        IJwtService jwtService,
        IQueryContext queryContext)
    {
        _tenantRepo = tenantRepo;
        _userRepo = userRepo;
        _auditRepo = auditRepo;
        _jwtService = jwtService;
        _queryContext = queryContext;
    }

    public async Task<RefreshTokenResult> HandleAsync(RefreshTokenCommand command, CancellationToken ct = default)
    {
        var tenant = await _tenantRepo.GetByIdAsync(_queryContext.TenantId, ct);
        var user = await _userRepo.GetByIdAsync(_queryContext.UserId, ct);

        var roleName = await _tenantRepo.GetUserRoleNameInTenantAsync(user.Id, tenant.Id, ct)
            ?? throw new NotFoundException("TenantRole", $"User role in tenant {tenant.Id} not found.");

        var token = _jwtService.GenerateToken(user, tenant.Id, roleName, out var jwtId, out var expiresAt);
        await _auditRepo.CreateSessionAsync(user.Id, tenant.Id, jwtId, _queryContext.IpAddress, expiresAt, ct: ct);

        return new RefreshTokenResult
        {
            Token = token,
            ExpiresAt = expiresAt,
            UserPublicId = user.PublicId,
            Email = user.Email,
            Name = user.Name,
            FirstName = user.FirstName,
            LastName = user.LastName,
            TenantPublicId = tenant.PublicId,
            TenantName = tenant.Name,
        };
    }
}
