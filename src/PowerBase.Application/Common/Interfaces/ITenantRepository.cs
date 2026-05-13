using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface ITenantRepository
{
    Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default);
    Task<long> CreateAsync(Tenant tenant, CancellationToken ct = default);
    Task<TenantRole> CreateRoleAsync(TenantRole role, CancellationToken ct = default);
    Task CreateTenantUserAsync(TenantUser tenantUser, CancellationToken ct = default);
}
