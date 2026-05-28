using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Apps.Queries.GetAppPermissions;

public record AppPermissionsResult(string? RoleName, IReadOnlySet<string> Permissions);

public class GetAppPermissionsQueryHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppUserRepository _appUserRepo;
    private readonly IQueryContext _queryContext;

    public GetAppPermissionsQueryHandler(
        IAppRepository appRepo,
        IAppUserRepository appUserRepo,
        IQueryContext queryContext)
    {
        _appRepo = appRepo;
        _appUserRepo = appUserRepo;
        _queryContext = queryContext;
    }

    public async Task<AppPermissionsResult> HandleAsync(GetAppPermissionsQuery query, CancellationToken ct = default)
    {
        var appId = await _appRepo.GetIdByPublicIdAsync(query.AppPublicId, ct);
        var permissions = await _appUserRepo.GetUserAppPermissionsAsync(appId, _queryContext.UserId, ct);
        var roleName = await _appUserRepo.GetUserRoleNameAsync(appId, _queryContext.UserId, ct);
        return new AppPermissionsResult(roleName, permissions);
    }
}
