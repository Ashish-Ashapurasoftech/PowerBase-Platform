using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Queries.GetFieldPermissions;

public class GetFieldPermissionsQueryHandler
{
    private readonly IAppRoleRepository _appRoleRepo;
    private readonly IAppRolePermissionRepository _permRepo;
    private readonly IAppTableRepository _tableRepo;

    public GetFieldPermissionsQueryHandler(
        IAppRoleRepository appRoleRepo,
        IAppRolePermissionRepository permRepo,
        IAppTableRepository tableRepo)
    {
        _appRoleRepo = appRoleRepo;
        _permRepo = permRepo;
        _tableRepo = tableRepo;
    }

    public async Task<IReadOnlyList<FieldPermissionRow>> HandleAsync(GetFieldPermissionsQuery query, CancellationToken ct = default)
    {
        var role = await _appRoleRepo.GetByPublicIdAsync(query.RolePublicId, ct)
                   ?? throw new NotFoundException("AppRole", query.RolePublicId);
        var table = await _tableRepo.GetByPublicIdAsync(query.TablePublicId, ct);
        return await _permRepo.GetFieldPermissionsAsync(role.Id, table.Id, ct);
    }
}
