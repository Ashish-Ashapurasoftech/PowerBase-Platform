using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Roles.Queries.ListPermissions;

public class ListPermissionsQueryHandler
{
    private readonly IPermissionRepository _permissionRepo;

    public ListPermissionsQueryHandler(IPermissionRepository permissionRepo) => _permissionRepo = permissionRepo;

    public async Task<IReadOnlyList<Permission>> HandleAsync(ListPermissionsQuery query, CancellationToken ct = default)
        => await _permissionRepo.GetAllAsync(ct);
}
