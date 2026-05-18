using System.Data;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IPermissionRepository
{
    Task<IReadOnlyList<Permission>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Permission>> GetByRoleIdAsync(long roleId, CancellationToken ct = default);
    Task AssignToRoleAsync(long roleId, IReadOnlyList<long> permissionIds, IDbTransaction? transaction = null, CancellationToken ct = default);
    Task ReplaceRolePermissionsAsync(long roleId, IReadOnlyList<long> permissionIds, CancellationToken ct = default);
}
