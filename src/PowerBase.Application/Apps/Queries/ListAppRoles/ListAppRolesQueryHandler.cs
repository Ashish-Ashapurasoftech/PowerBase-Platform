using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Apps.Queries.ListAppRoles;

public record AppRoleResult(
    Guid PublicId,
    string Name,
    bool IsDefault,
    bool IsSystem,
    bool CanViewRecords,
    bool CanAddRecords,
    bool CanEditRecords,
    bool CanDeleteRecords);

public class ListAppRolesQueryHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppRoleRepository _appRoleRepo;

    public ListAppRolesQueryHandler(IAppRepository appRepo, IAppRoleRepository appRoleRepo)
    {
        _appRepo = appRepo;
        _appRoleRepo = appRoleRepo;
    }

    public async Task<IReadOnlyList<AppRoleResult>> HandleAsync(ListAppRolesQuery query, CancellationToken ct = default)
    {
        var appId = await _appRepo.GetIdByPublicIdAsync(query.AppPublicId, ct);
        var roles = await _appRoleRepo.ListByAppIdAsync(appId, ct);
        return roles.Select(r => new AppRoleResult(
            r.PublicId, r.Name, r.IsDefault, r.IsSystem,
            r.CanViewRecords, r.CanAddRecords, r.CanEditRecords, r.CanDeleteRecords)).ToList();
    }
}
