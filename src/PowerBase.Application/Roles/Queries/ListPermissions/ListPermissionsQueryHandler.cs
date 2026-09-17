using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Roles.Queries.ListPermissions;

public class ListPermissionsQueryHandler
{
    private readonly IPermissionRepository _permissionRepo;
    private readonly IQueryContext _queryContext;

    public ListPermissionsQueryHandler(IPermissionRepository permissionRepo, IQueryContext queryContext)
    {
        _permissionRepo = permissionRepo;
        _queryContext = queryContext;
    }

    public async Task<IReadOnlyList<Permission>> HandleAsync(ListPermissionsQuery query, CancellationToken ct = default)
    {
        var permissions = await _permissionRepo.GetAllAsync(ct);
        if (_queryContext.IsSuperAdmin || _queryContext.IsTenantAdmin) return permissions;
        // Match the permissions a delegated manager is allowed to assign.
        return permissions.Where(permission => _queryContext.Permissions.Contains(permission.Code)).ToList();
    }
}
