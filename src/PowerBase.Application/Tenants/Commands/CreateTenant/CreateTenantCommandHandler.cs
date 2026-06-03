using System.Text.RegularExpressions;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Tenants.Commands.CreateTenant;

public class CreateTenantResult
{
    public string Token { get; init; } = string.Empty;
    public DateTime ExpiresAt { get; init; }
    public Guid UserPublicId { get; init; }
    public string Email { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public Guid TenantPublicId { get; init; }
    public string TenantName { get; init; } = string.Empty;
    public string TenantSlug { get; init; } = string.Empty;
}

public class CreateTenantCommandHandler
{
    private readonly ITenantRepository _tenantRepo;
    private readonly IUserRepository _userRepo;
    private readonly IPermissionRepository _permissionRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly IJwtService _jwtService;
    private readonly IControlUnitOfWork _uow;
    private readonly IQueryContext _queryContext;
    private readonly ITenantProvisioningService _provisioningService;

    public CreateTenantCommandHandler(
        ITenantRepository tenantRepo,
        IUserRepository userRepo,
        IPermissionRepository permissionRepo,
        IAuditRepository auditRepo,
        IJwtService jwtService,
        IControlUnitOfWork uow,
        IQueryContext queryContext,
        ITenantProvisioningService provisioningService)
    {
        _tenantRepo = tenantRepo;
        _userRepo = userRepo;
        _permissionRepo = permissionRepo;
        _auditRepo = auditRepo;
        _jwtService = jwtService;
        _uow = uow;
        _queryContext = queryContext;
        _provisioningService = provisioningService;
    }

    public async Task<CreateTenantResult> HandleAsync(CreateTenantCommand command, CancellationToken ct = default)
    {
        var validator = new CreateTenantCommandValidator();
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(
                validation.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        var slug = await GenerateUniqueSlugAsync(command.Name, ct);
        var tenantPublicId = Guid.NewGuid();

        var tenant = new Tenant
        {
            PublicId = tenantPublicId,
            Name = command.Name,
            Slug = slug,
            Status = "Active",
        };

        var adminRole = new TenantRole { Name = DefaultTenantRoles.Administrator, IsDefault = true, IsSystem = true };
        var userRole = new TenantRole { Name = DefaultTenantRoles.User, IsDefault = false, IsSystem = false };

        await _uow.BeginAsync(ct);
        try
        {
            var tenantId = await _tenantRepo.CreateAsync(tenant, _uow.Transaction, ct);

            adminRole.TenantId = tenantId;
            userRole.TenantId = tenantId;

            var adminRoleId = await _tenantRepo.CreateRoleAsync(adminRole, _uow.Transaction, ct);
            var userRoleId = await _tenantRepo.CreateRoleAsync(userRole, _uow.Transaction, ct);

            var allPerms = await _permissionRepo.GetAllAsync(ct);
            var adminPermIds = allPerms.Select(p => p.Id).ToList();
            var userPermIds = allPerms.Where(p => PermissionCodes.DefaultUserPermissions.Contains(p.Code)).Select(p => p.Id).ToList();
            await _permissionRepo.AssignToRoleAsync(adminRoleId, adminPermIds, _uow.Transaction, ct);
            await _permissionRepo.AssignToRoleAsync(userRoleId, userPermIds, _uow.Transaction, ct);

            await _tenantRepo.CreateTenantUserAsync(new TenantUser
            {
                TenantId = tenantId,
                UserId = _queryContext.UserId,
                TenantRoleId = adminRoleId,
                IsOwner = true,
                IsActive = true,
            }, _uow.Transaction, ct);

            await _uow.CommitAsync(ct);

            // Provision the tenant's isolated database (CREATE DB + baseline migrations).
            // This runs outside the control transaction — on failure the tenant row is marked Failed.
            await _provisioningService.ProvisionAsync(tenantId, ct);

            var user = await _userRepo.GetByIdAsync(_queryContext.UserId, ct);
            var token = _jwtService.GenerateToken(user, tenantId, out var jwtId, out var expiresAt);
            await _auditRepo.CreateSessionAsync(user.Id, tenantId, jwtId, _queryContext.IpAddress, expiresAt, ct: ct);

            return new CreateTenantResult
            {
                Token = token,
                ExpiresAt = expiresAt,
                UserPublicId = user.PublicId,
                Email = user.Email,
                Name = user.Name,
                TenantPublicId = tenantPublicId,
                TenantName = command.Name,
                TenantSlug = slug,
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
