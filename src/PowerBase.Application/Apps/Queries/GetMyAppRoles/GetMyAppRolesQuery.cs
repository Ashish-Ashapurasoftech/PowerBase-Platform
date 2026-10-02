using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Apps.Queries.GetMyAppRoles;

public record GetMyAppRolesQuery(Guid AppPublicId);

/// <summary>Every role NAME the current user holds in this app — direct assignments plus
/// group-derived ones. A separate result from AppPermissionsResult on purpose: that one carries a
/// single RoleName that lots of existing code reads, so it is left untouched.</summary>
public record MyAppRolesResult(IReadOnlyList<string> RoleNames);

public class GetMyAppRolesQueryHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppUserRepository _appUserRepo;
    private readonly IQueryContext _queryContext;

    public GetMyAppRolesQueryHandler(IAppRepository appRepo, IAppUserRepository appUserRepo, IQueryContext queryContext)
    {
        _appRepo = appRepo;
        _appUserRepo = appUserRepo;
        _queryContext = queryContext;
    }

    public async Task<MyAppRolesResult> HandleAsync(GetMyAppRolesQuery query, CancellationToken ct = default)
    {
        var appId = await _appRepo.GetIdByPublicIdAsync(query.AppPublicId, ct);
        var names = await _appUserRepo.GetUserAppRoleNamesAsync(appId, _queryContext.UserId, ct);
        return new MyAppRolesResult(names);
    }
}
