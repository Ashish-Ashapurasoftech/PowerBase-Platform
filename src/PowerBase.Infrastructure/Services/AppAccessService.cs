using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Infrastructure.Services;

public class AppAccessService : IAppAccessService
{
    private readonly IAppRepository _appRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IReportRepository _reportRepo;
    private readonly IAppUserRepository _appUserRepo;
    private readonly IQueryContext _queryContext;

    public AppAccessService(
        IAppRepository appRepo,
        IAppTableRepository tableRepo,
        IReportRepository reportRepo,
        IAppUserRepository appUserRepo,
        IQueryContext queryContext)
    {
        _appRepo = appRepo;
        _tableRepo = tableRepo;
        _reportRepo = reportRepo;
        _appUserRepo = appUserRepo;
        _queryContext = queryContext;
    }

    public async Task RequireByAppPublicIdAsync(Guid appPublicId, AppAccess required, CancellationToken ct = default)
    {
        var appId = await _appRepo.GetIdByPublicIdAsync(appPublicId, ct);
        await RequireByAppIdAsync(appId, required, ct);
    }

    public async Task RequireByTablePublicIdAsync(Guid tablePublicId, AppAccess required, CancellationToken ct = default)
    {
        var appId = await _tableRepo.GetAppIdByPublicIdAsync(tablePublicId, ct);
        await RequireByAppIdAsync(appId, required, ct);
    }

    public async Task RequireByReportPublicIdAsync(Guid reportPublicId, AppAccess required, CancellationToken ct = default)
    {
        var appId = await _reportRepo.GetAppIdByPublicIdAsync(reportPublicId, ct);
        await RequireByAppIdAsync(appId, required, ct);
    }

    public async Task RequireByAppIdAsync(long appId, AppAccess required, CancellationToken ct = default)
    {
        var roleName = await _appUserRepo.GetUserRoleNameAsync(appId, _queryContext.UserId, ct);

        if (roleName is null)
            throw new UnauthorizedActionException("You do not have access to this app.");

        if (required == AppAccess.Admin && roleName != "Administrator")
            throw new UnauthorizedActionException("Administrator access is required for this action.");
    }
}
