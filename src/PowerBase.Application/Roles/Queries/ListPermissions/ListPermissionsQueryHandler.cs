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
        return await _permissionRepo.GetAllAsync(ct);
    }
}
