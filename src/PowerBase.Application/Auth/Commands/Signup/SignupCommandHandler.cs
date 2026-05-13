using System.Text.RegularExpressions;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Auth.Commands.Signup;

public class SignupCommandResult
{
    public string Token { get; init; } = string.Empty;
    public DateTime ExpiresAt { get; init; }
    public long UserId { get; init; }
    public long TenantId { get; init; }
    public Guid UserPublicId { get; init; }
    public string Email { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}

public class SignupCommandHandler
{
    private readonly IUserRepository _userRepo;
    private readonly ITenantRepository _tenantRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly IJwtService _jwtService;
    private readonly IPasswordService _passwordService;
    private readonly IUnitOfWork _uow;
    private readonly IQueryContext _queryContext;

    public SignupCommandHandler(
        IUserRepository userRepo,
        ITenantRepository tenantRepo,
        IAuditRepository auditRepo,
        IJwtService jwtService,
        IPasswordService passwordService,
        IUnitOfWork uow,
        IQueryContext queryContext)
    {
        _userRepo = userRepo;
        _tenantRepo = tenantRepo;
        _auditRepo = auditRepo;
        _jwtService = jwtService;
        _passwordService = passwordService;
        _uow = uow;
        _queryContext = queryContext;
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

        var existing = await _userRepo.GetByEmailAsync(command.Email, ct);
        if (existing is not null)
            throw new DuplicateException("User", "email", command.Email);

        var hashedPassword = _passwordService.Hash(command.Password);
        var slug = await GenerateUniqueSlugAsync(command.TenantName, ct);

        var user = new User
        {
            Email = command.Email,
            HashedPassword = hashedPassword,
            Name = command.Name,
            IsActive = true,
        };

        var tenant = new Tenant
        {
            Name = command.TenantName,
            Slug = slug,
            Status = "Active",
        };

        await _uow.BeginAsync(ct);
        try
        {
            var userId = await _userRepo.CreateAsync(user, _uow.Transaction, ct);
            var tenantId = await _tenantRepo.CreateAsync(tenant, _uow.Transaction, ct);

            var adminRole = new TenantRole { TenantId = tenantId, Name = DefaultTenantRoles.Administrator, IsDefault = true, IsSystem = true };
            var userRole = new TenantRole { TenantId = tenantId, Name = DefaultTenantRoles.User, IsDefault = false, IsSystem = false };

            var adminRoleId = await _tenantRepo.CreateRoleAsync(adminRole, _uow.Transaction, ct);
            await _tenantRepo.CreateRoleAsync(userRole, _uow.Transaction, ct);

            await _tenantRepo.CreateTenantUserAsync(
                new TenantUser { TenantId = tenantId, UserId = userId, TenantRoleId = adminRoleId, IsOwner = true, IsActive = true },
                _uow.Transaction, ct);

            await _uow.CommitAsync(ct);

            var tokenUser = new User { Id = userId, PublicId = user.PublicId, Email = user.Email, Name = user.Name };
            var token = _jwtService.GenerateToken(tokenUser, tenantId, out var jwtId);
            var expiresAt = DateTime.UtcNow.AddMinutes(1440);

            await _auditRepo.CreateSessionAsync(userId, tenantId, jwtId, _queryContext.IpAddress, expiresAt, ct);

            var created = await _userRepo.GetByIdAsync(userId, ct);

            return new SignupCommandResult
            {
                Token = token,
                ExpiresAt = expiresAt,
                UserId = userId,
                TenantId = tenantId,
                UserPublicId = created.PublicId,
                Email = created.Email,
                Name = created.Name,
            };
        }
        catch
        {
            await _uow.RollbackAsync(ct);
            throw;
        }
    }

    private async Task<string> GenerateUniqueSlugAsync(string tenantName, CancellationToken ct)
    {
        var baseSlug = Regex.Replace(tenantName.ToLowerInvariant().Trim(), @"[^a-z0-9]+", "-").Trim('-');
        var slug = baseSlug;
        var counter = 1;
        while (await _tenantRepo.SlugExistsAsync(slug, ct))
            slug = $"{baseSlug}-{counter++}";
        return slug;
    }
}
