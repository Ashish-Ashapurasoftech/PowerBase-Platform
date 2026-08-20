using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;

namespace PowerBase.Application.Tables.Queries.ListTableNavItems;

/// <summary>Backs the dedicated nav-list endpoint (GET /apps/{appId}/tables/nav) consumed by the
/// sidebar, top nav, and table switcher — unlike <c>ListTablesQueryHandler</c>, this always returns
/// every table in the app in one unpaginated call; consumers filter (isShowInBar) and search
/// (by name/singularLabel) over the result client-side.</summary>
public class ListTableNavItemsQueryHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppTableRepository _tableRepo;

    public ListTableNavItemsQueryHandler(IAppRepository appRepo, IAppTableRepository tableRepo)
    {
        _appRepo = appRepo;
        _tableRepo = tableRepo;
    }

    public async Task<IReadOnlyList<AppTableNavItemDto>> HandleAsync(ListTableNavItemsQuery query, CancellationToken ct = default)
    {
        var app = await _appRepo.GetByPublicIdAsync(query.AppPublicId, ct);
        return await _tableRepo.ListNavByAppAsync(app.Id, ct);
    }
}
