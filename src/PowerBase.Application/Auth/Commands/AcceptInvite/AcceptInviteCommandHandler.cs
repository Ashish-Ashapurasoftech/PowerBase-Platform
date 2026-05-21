using System.Security.Cryptography;
using System.Text;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Tenants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Auth.Commands.AcceptInvite;

public class AcceptInviteResult
{
    public string IdentityToken { get; init; } = string.Empty;
    public DateTime ExpiresAt { get; init; }
    public Guid UserPublicId { get; init; }
    public string Email { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<TenantItem> Tenants { get; init; } = [];
}

public class AcceptInviteCommandHandler
{
    private readonly IAuditRepository _auditRepo;
    private readonly IUserRepository _userRepo;
    private readonly ITenantRepository _tenantRepo;
    private readonly IPasswordService _passwordService;
    private readonly IJwtService _jwtService;

    public AcceptInviteCommandHandler(
        IAuditRepository auditRepo,
        IUserRepository userRepo,
        ITenantRepository tenantRepo,
        IPasswordService passwordService,
        IJwtService jwtService)
    {
        _auditRepo = auditRepo;
        _userRepo = userRepo;
        _tenantRepo = tenantRepo;
        _passwordService = passwordService;
        _jwtService = jwtService;
    }

    public async Task<AcceptInviteResult> HandleAsync(AcceptInviteCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            throw new ValidationException(new Dictionary<string, string[]> { ["Name"] = ["Name is required."] });
        if (string.IsNullOrWhiteSpace(command.Password) || command.Password.Length < 8)
            throw new ValidationException(new Dictionary<string, string[]> { ["Password"] = ["Password must be at least 8 characters."] });

        var tokenHash = ComputeSha256(command.Token);
        var tokenRecord = await _auditRepo.GetInviteTokenByHashAsync(tokenHash, ct)
            ?? throw new NotFoundException("InviteToken", "token");

        if (tokenRecord.UsedOn.HasValue)
            throw new ValidationException(new Dictionary<string, string[]> { ["Token"] = ["This invite link has already been used."] });
        if (tokenRecord.ExpiresOn < DateTime.UtcNow)
            throw new ValidationException(new Dictionary<string, string[]> { ["Token"] = ["This invite link has expired."] });

        var hashedPassword = _passwordService.Hash(command.Password);
        await _userRepo.ActivateAsync(tokenRecord.UserId, command.Name, hashedPassword, ct);
        await _tenantRepo.ActivateTenantUserAsync(tokenRecord.UserId, tokenRecord.TenantId, ct);
        await _auditRepo.ConsumeInviteTokenAsync(tokenRecord.Id, ct);

        var user = await _userRepo.GetByIdAsync(tokenRecord.UserId, ct);
        var tenants = await _tenantRepo.ListTenantsForUserAsync(tokenRecord.UserId, ct);
        var identityToken = _jwtService.GenerateIdentityToken(user, out _, out var expiresAt);

        return new AcceptInviteResult
        {
            IdentityToken = identityToken,
            ExpiresAt = expiresAt,
            UserPublicId = user.PublicId,
            Email = user.Email,
            Name = user.Name,
            Tenants = tenants,
        };
    }

    private static string ComputeSha256(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
