using System.Text.RegularExpressions;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Enums;
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
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
}

public class SignupCommandHandler
{
    private readonly IUserRepository _userRepo;
    private readonly ITenantRepository _tenantRepo;
    private readonly ISystemRoleRepository _systemRoleRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly IJwtService _jwtService;
    private readonly IPasswordService _passwordService;
    private readonly IUnitOfWork _uow;
    private readonly IQueryContext _queryContext;

    public SignupCommandHandler(
        IUserRepository userRepo,
        ITenantRepository tenantRepo,
        ISystemRoleRepository systemRoleRepo,
        IAuditRepository auditRepo,
        IJwtService jwtService,
        IPasswordService passwordService,
        IUnitOfWork uow,
        IQueryContext queryContext)
    {
        _userRepo = userRepo;
        _tenantRepo = tenantRepo;
        _systemRoleRepo = systemRoleRepo;
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

        var systemRoleId = await _systemRoleRepo.GetIdByCodeAsync(SystemRoleCodes.User, ct);
        var passwordHash = _passwordService.Hash(command.Password);
        var slug = await GenerateUniqueSlugAsync(command.TenantName, ct);

        var user = new User
        {
            Email = command.Email,
            PasswordHash = passwordHash,
            FirstName = command.FirstName,
            LastName = command.LastName,
            SystemRoleId = systemRoleId,
            IsActive = true,
        };

        var tenant = new Tenant
        {
            Name = command.TenantName,
            Slug = slug,
            Status = TenantStatus.Active,
        };

        await _uow.BeginAsync(ct);
        try
        {
            var userId = await _userRepo.CreateAsync(user, _uow.Transaction, ct);
            var tenantId = await _tenantRepo.CreateAsync(tenant, _uow.Transaction, ct);

            var adminRole = new TenantRole { TenantId = tenantId, Name = DefaultTenantRoles.Administrator, IsDefault = true };
            var userRole = new TenantRole { TenantId = tenantId, Name = DefaultTenantRoles.User, IsDefault = false };

            var adminRoleId = await _tenantRepo.CreateRoleAsync(adminRole, _uow.Transaction, ct);
            await _tenantRepo.CreateRoleAsync(userRole, _uow.Transaction, ct);

            await _tenantRepo.CreateTenantUserAsync(
                new TenantUser { TenantId = tenantId, UserId = userId, TenantRoleId = adminRoleId, IsActive = true },
                _uow.Transaction, ct);

            await _uow.CommitAsync(ct);

            var token = _jwtService.GenerateToken(
                new User { Id = userId, PublicId = user.PublicId, Email = user.Email, FirstName = user.FirstName, LastName = user.LastName },
                tenantId, out var jwtId);

            var expiresAt = DateTime.UtcNow.AddDays(1);
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
                FirstName = created.FirstName,
                LastName = created.LastName,
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
