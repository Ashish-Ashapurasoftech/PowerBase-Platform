using System.Data;
using PowerBase.Application.Tenants;
using PowerBase.Application.Users;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface ITenantRepository
{
    Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default);
    Task<string?> GetTenantNameByIdAsync(long tenantId, CancellationToken ct = default);
    Task<long> GetActiveTenantIdByUserIdAsync(long userId, CancellationToken ct = default);
    Task<IReadOnlyList<TenantItem>> ListTenantsForUserAsync(long userId, CancellationToken ct = default);
    Task<Tenant> GetTenantForUserAsync(Guid tenantPublicId, long userId, CancellationToken ct = default);
    Task<long> CreateAsync(Tenant tenant, IDbTransaction? transaction = null, CancellationToken ct = default);
    Task<long> CreateRoleAsync(TenantRole role, IDbTransaction? transaction = null, CancellationToken ct = default);
    Task CreateTenantUserAsync(TenantUser tenantUser, IDbTransaction? transaction = null, CancellationToken ct = default);
    Task UpsertTenantUserAsync(TenantUser tenantUser, CancellationToken ct = default);

    // User management
    Task<IReadOnlyList<TenantUserDetail>> ListUsersAsync(CancellationToken ct = default);
    Task<TenantUser?> GetTenantUserByUserPublicIdAsync(Guid userPublicId, CancellationToken ct = default);
    Task<bool> IsUserInTenantAsync(long userId, CancellationToken ct = default);
    Task<bool> IsActiveMemberAsync(long userId, CancellationToken ct = default);
    Task UpdateTenantUserRoleAsync(long tenantUserId, long tenantRoleId, CancellationToken ct = default);
    Task RemoveTenantUserAsync(long tenantUserId, CancellationToken ct = default);
    Task ActivateTenantUserAsync(long userId, long tenantId, CancellationToken ct = default);

    // Role management
    Task<IReadOnlyList<TenantRole>> ListRolesAsync(CancellationToken ct = default);
    Task<TenantRole?> GetRoleByIdAsync(long id, CancellationToken ct = default);
    Task<TenantRole?> GetRoleByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task<bool> RoleNameExistsAsync(string name, CancellationToken ct = default);
    Task<int> UpdateRoleAsync(Guid publicId, string name, string? description, CancellationToken ct = default);
    Task<int> DeleteRoleAsync(Guid publicId, CancellationToken ct = default);
    Task<int> CountRoleMembersAsync(long roleId, CancellationToken ct = default);
}
