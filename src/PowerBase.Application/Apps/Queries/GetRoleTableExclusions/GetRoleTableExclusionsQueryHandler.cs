using PowerBase.Application.Apps.Commands.UpdateAppRole;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Queries.GetRoleTableExclusions;

public record GetRoleTableExclusionsQuery(Guid AppPublicId, Guid RolePublicId);

/// <summary>Roles > Advanced Permissions > Table selection: the tables each of the role's builder permissions does not apply to.</summary>
public class GetRoleTableExclusionsQueryHandler(IAppRepository appRepo, IAppRoleRepository appRoleRepo, IAppRolePermissionRepository permRepo)
{
    public async Task<IReadOnlyList<TableScopeExclusion>> HandleAsync(GetRoleTableExclusionsQuery query, CancellationToken ct = default)
    {
        var appId = await appRepo.GetIdByPublicIdAsync(query.AppPublicId, ct);
        var role = await appRoleRepo.GetByPublicIdAsync(query.RolePublicId, ct);
        if (role is null || role.AppId != appId)
            throw new NotFoundException("AppRole", query.RolePublicId);
        var rows = await permRepo.GetTableExclusionsAsync([role.Id], ct);
        return rows.Select(r => new TableScopeExclusion(r.Code, r.TablePublicId)).ToList();
    }
}
