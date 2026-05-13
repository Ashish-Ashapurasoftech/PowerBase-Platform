using System.Data;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface ITenantRepository
{
    Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default);
    Task<long> GetActiveTenantIdByUserIdAsync(long userId, CancellationToken ct = default);
    Task<long> CreateAsync(Tenant tenant, IDbTransaction? transaction = null, CancellationToken ct = default);
    Task<long> CreateRoleAsync(TenantRole role, IDbTransaction? transaction = null, CancellationToken ct = default);
    Task CreateTenantUserAsync(TenantUser tenantUser, IDbTransaction? transaction = null, CancellationToken ct = default);
}
