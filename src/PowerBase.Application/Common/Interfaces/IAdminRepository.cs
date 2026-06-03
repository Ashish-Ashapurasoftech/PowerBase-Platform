using PowerBase.Application.Admin;

namespace PowerBase.Application.Common.Interfaces;

public interface IAdminRepository
{
    // Tenants
    Task<IReadOnlyList<AdminTenantDto>> ListTenantsAsync(int page, int pageSize, string? search, CancellationToken ct = default);
    Task<int> CountTenantsAsync(string? search, CancellationToken ct = default);
    Task UpdateTenantStatusAsync(long tenantId, string status, CancellationToken ct = default);

    // Users
    Task<IReadOnlyList<AdminUserDto>> ListUsersAsync(int page, int pageSize, string? search, CancellationToken ct = default);
    Task<int> CountUsersAsync(string? search, CancellationToken ct = default);
    Task UpdateUserActiveStateAsync(Guid userPublicId, bool isActive, CancellationToken ct = default);
    Task UpdateUserSystemRoleAsync(Guid userPublicId, int? systemRoleId, CancellationToken ct = default);

    Task<long?> GetTenantIdByPublicIdAsync(Guid tenantPublicId, CancellationToken ct = default);

    // Tenant members
    Task<IReadOnlyList<AdminTenantMemberDto>> ListTenantMembersAsync(long tenantId, CancellationToken ct = default);
    Task<long?> GetUserIdByEmailAsync(string email, CancellationToken ct = default);
    Task AssignUserToTenantAsync(long tenantId, long userId, long roleId, long assignedBy, bool isActive = true, CancellationToken ct = default);
    Task RemoveUserFromTenantAsync(long tenantId, Guid userPublicId, CancellationToken ct = default);

    // Tenant roles
    Task<IReadOnlyList<AdminTenantRoleDto>> ListTenantRolesAsync(long tenantId, CancellationToken ct = default);
    Task<long?> GetTenantRoleIdByPublicIdAsync(long tenantId, Guid rolePublicId, CancellationToken ct = default);

    // New: tenant status guard, assignable users, role change, permissions
    Task<string?> GetTenantStatusAsync(long tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<AdminUserDto>> ListUsersNotInTenantAsync(long tenantId, string? search, CancellationToken ct = default);
    Task ChangeMemberRoleAsync(long tenantId, Guid userPublicId, long newRoleId, CancellationToken ct = default);
    Task<IReadOnlyList<AdminRolePermissionDto>> GetTenantRolePermissionsAsync(long tenantId, Guid rolePublicId, CancellationToken ct = default);
    Task ReplaceRolePermissionsAdminAsync(long tenantId, Guid rolePublicId, IReadOnlyList<string> permissionCodes, CancellationToken ct = default);
    Task<IReadOnlyList<AdminPermissionDto>> ListAllPermissionsAsync(CancellationToken ct = default);

    // Audit
    Task<(IReadOnlyList<AdminLoginAttemptDto> Items, int Total)> ListLoginAttemptsAsync(int page, int pageSize, string? email, CancellationToken ct = default);
}
