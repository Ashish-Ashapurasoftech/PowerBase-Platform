using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Roles.Queries.GetRolePermissions;

public class GetRolePermissionsQueryHandler
{
    private readonly ITenantRepository _tenantRepo;
    private readonly IPermissionRepository _permissionRepo;

    public GetRolePermissionsQueryHandler(ITenantRepository tenantRepo, IPermissionRepository permissionRepo)
    {
        _tenantRepo = tenantRepo;
        _permissionRepo = permissionRepo;
    }

    public async Task<IReadOnlyList<Permission>> HandleAsync(GetRolePermissionsQuery query, CancellationToken ct = default)
    {
        var role = await _tenantRepo.GetRoleByPublicIdAsync(query.RolePublicId, ct)
            ?? throw new NotFoundException("TenantRole", query.RolePublicId);

        return await _permissionRepo.GetByRoleIdAsync(role.Id, ct);
    }
}
