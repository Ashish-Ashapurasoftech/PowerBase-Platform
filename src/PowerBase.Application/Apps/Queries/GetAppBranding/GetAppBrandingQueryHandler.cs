using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Apps.Queries.GetAppBranding;

public class GetAppBrandingQueryHandler
{
    private readonly IAppRepository _appRepo;

    public GetAppBrandingQueryHandler(IAppRepository appRepo)
    {
        _appRepo = appRepo;
    }

    public async Task<App> HandleAsync(GetAppBrandingQuery query, CancellationToken ct = default)
    {
        return await _appRepo.GetByPublicIdAsync(query.AppPublicId, ct);
    }
}
