using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Queries.GetTablePermissions;

public class GetTablePermissionsQueryHandler
{
    private readonly IAppRoleRepository _appRoleRepo;
    private readonly IAppRolePermissionRepository _permRepo;

    public GetTablePermissionsQueryHandler(IAppRoleRepository appRoleRepo, IAppRolePermissionRepository permRepo)
    {
        _appRoleRepo = appRoleRepo;
        _permRepo = permRepo;
    }

    public async Task<IReadOnlyList<TablePermissionRow>> HandleAsync(GetTablePermissionsQuery query, CancellationToken ct = default)
    {
        var role = await _appRoleRepo.GetByPublicIdAsync(query.RolePublicId, ct)
                   ?? throw new NotFoundException("AppRole", query.RolePublicId);
        return await _permRepo.GetTablePermissionsAsync(role.Id, ct);
    }
}
