using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Pages.Queries.ResolveHomePage;

public record ResolveHomePageQuery(Guid AppPublicId);

/// <summary>PagePublicId is null when neither the user's roles nor the app have a (visible) home Dashboard.</summary>
public record ResolvedHomePage(Guid? PagePublicId, string Source);

public class ResolveHomePageQueryHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppUserRepository _appUserRepo;
    private readonly IAppRoleRepository _appRoleRepo;
    private readonly IPageRepository _pageRepo;
    private readonly IQueryContext _queryContext;

    public ResolveHomePageQueryHandler(
        IAppRepository appRepo, IAppUserRepository appUserRepo, IAppRoleRepository appRoleRepo,
        IPageRepository pageRepo, IQueryContext queryContext)
    {
        _appRepo = appRepo;
        _appUserRepo = appUserRepo;
        _appRoleRepo = appRoleRepo;
        _pageRepo = pageRepo;
        _queryContext = queryContext;
    }

    /// <summary>A user's home Dashboard: the home page of their best-ranked role that has one (lowest Rank first,
    /// unranked last), else the app-wide default home. Only Dashboards the user can actually see qualify.</summary>
    public async Task<ResolvedHomePage> HandleAsync(ResolveHomePageQuery query, CancellationToken ct = default)
    {
        var appId = await _appRepo.GetIdByPublicIdAsync(query.AppPublicId, ct);
        var visible = (await _pageRepo.ListVisibleByAppAsync(appId, null, ct))
            .Where(p => p.PageType == PageTypes.Dashboard)
            .ToList();

        var myRoleIds = (await _appUserRepo.GetUserAppRoleIdsAsync(appId, _queryContext.UserId, ct)).ToHashSet();
        var roles = await _appRoleRepo.ListDetailsByAppIdAsync(appId, ct);
        var roleHome = roles
            .Where(r => myRoleIds.Contains(r.Id) && r.HomePagePublicId.HasValue)
            .OrderBy(r => r.Rank ?? int.MaxValue).ThenBy(r => r.Id)
            .Select(r => r.HomePagePublicId!.Value)
            .FirstOrDefault(id => visible.Any(p => p.PublicId == id));
        if (roleHome != Guid.Empty)
            return new ResolvedHomePage(roleHome, "Role");

        var appDefault = visible.FirstOrDefault(p => p.IsDefaultHome);
        return appDefault is null
            ? new ResolvedHomePage(null, "None")
            : new ResolvedHomePage(appDefault.PublicId, "App");
    }
}
