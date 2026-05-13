using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Apps.Queries.GetApp;

public class GetAppQueryHandler
{
    private readonly IAppRepository _appRepo;

    public GetAppQueryHandler(IAppRepository appRepo)
    {
        _appRepo = appRepo;
    }

    public async Task<App> HandleAsync(GetAppQuery query, CancellationToken ct = default)
    {
        return await _appRepo.GetByPublicIdAsync(query.PublicId, ct);
    }
}
