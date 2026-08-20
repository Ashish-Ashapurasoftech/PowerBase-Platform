using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Infrastructure.Services;

public class AppAccessService : IAppAccessService
{
    private readonly IAppRepository _appRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IReportRepository _reportRepo;
    private readonly IFormRepository _formRepo;
    private readonly IFormRuleRepository _formRuleRepo;
    private readonly IPageRepository _pageRepo;
    private readonly IAppUserRepository _appUserRepo;
    private readonly IQueryContext _queryContext;
    private readonly IPipelineRepository _pipelineRepo;

    public AppAccessService(
        IAppRepository appRepo,
        IAppTableRepository tableRepo,
        IReportRepository reportRepo,
        IFormRepository formRepo,
        IFormRuleRepository formRuleRepo,
        IPageRepository pageRepo,
        IAppUserRepository appUserRepo,
        IQueryContext queryContext,
        IPipelineRepository pipelineRepo)
    {
        _appRepo = appRepo;
        _tableRepo = tableRepo;
        _reportRepo = reportRepo;
        _formRepo = formRepo;
        _formRuleRepo = formRuleRepo;
        _pageRepo = pageRepo;
        _appUserRepo = appUserRepo;
        _queryContext = queryContext;
        _pipelineRepo = pipelineRepo;
    }

    public async Task RequirePermissionByAppPublicIdAsync(Guid appPublicId, string permissionCode, CancellationToken ct = default)
    {
        var appId = await _appRepo.GetIdByPublicIdAsync(appPublicId, ct);
        await RequirePermissionByAppIdAsync(appId, permissionCode, ct);
    }

    public async Task RequirePermissionByTablePublicIdAsync(Guid tablePublicId, string permissionCode, CancellationToken ct = default)
    {
        var appId = await _tableRepo.GetAppIdByPublicIdAsync(tablePublicId, ct);
        await RequirePermissionByAppIdAsync(appId, permissionCode, ct);
    }

    public async Task RequirePermissionByReportPublicIdAsync(Guid reportPublicId, string permissionCode, CancellationToken ct = default)
    {
        var appId = await _reportRepo.GetAppIdByPublicIdAsync(reportPublicId, ct);
        await RequirePermissionByAppIdAsync(appId, permissionCode, ct);
    }

    public async Task RequirePermissionByFormPublicIdAsync(Guid formPublicId, string permissionCode, CancellationToken ct = default)
    {
        var appId = await _formRepo.GetAppIdByPublicIdAsync(formPublicId, ct);
        await RequirePermissionByAppIdAsync(appId, permissionCode, ct);
    }

    public async Task RequirePermissionByFormRulePublicIdAsync(Guid rulePublicId, string permissionCode, CancellationToken ct = default)
    {
        var appId = await _formRuleRepo.GetAppIdByPublicIdAsync(rulePublicId, ct);
        await RequirePermissionByAppIdAsync(appId, permissionCode, ct);
    }

    public async Task RequirePermissionByPagePublicIdAsync(Guid pagePublicId, string permissionCode, CancellationToken ct = default)
    {
        var appId = await _pageRepo.GetAppIdByPublicIdAsync(pagePublicId, ct);
        await RequirePermissionByAppIdAsync(appId, permissionCode, ct);
    }

    public async Task RequirePermissionByPipelinePublicIdAsync(Guid pipelinePublicId, string permissionCode, CancellationToken ct = default)
    {
        var pipeline = await _pipelineRepo.GetByPublicIdAsync(pipelinePublicId, ct);
        
        if (pipeline.CreatedBy != _queryContext.UserId)
        {
            throw new UnauthorizedActionException("You do not own this pipeline.");
        }
        
        await RequirePermissionByAppIdAsync(pipeline.AppId, permissionCode, ct);
    }

    public async Task RequirePermissionByAppIdAsync(long appId, string permissionCode, CancellationToken ct = default)
    {
        var permissions = await _appUserRepo.GetUserAppPermissionsAsync(appId, _queryContext.UserId, ct);
        if (!permissions.Contains(permissionCode))
        {
            throw new UnauthorizedActionException("You do not have permission to perform this action in this app.");
        }
    }

    public async Task RequireAppRoleAsync(Guid appPublicId, string roleName, CancellationToken ct = default)
    {
        var appId = await _appRepo.GetIdByPublicIdAsync(appPublicId, ct);
        var actualRoleName = await _appUserRepo.GetUserRoleNameAsync(appId, _queryContext.UserId, ct);

        if (actualRoleName != roleName)
        {
            throw new UnauthorizedActionException($"This action requires the '{roleName}' app role.");
        }
    }

    public async Task RequireMembershipByTablePublicIdAsync(Guid tablePublicId, CancellationToken ct = default)
    {
        if (_queryContext.IsSuperAdmin) return;
        var appId = await _tableRepo.GetAppIdByPublicIdAsync(tablePublicId, ct);
        await RequireMembershipByAppIdAsync(appId, ct);
    }

    public async Task RequireMembershipByReportPublicIdAsync(Guid reportPublicId, CancellationToken ct = default)
    {
        if (_queryContext.IsSuperAdmin) return;
        var appId = await _reportRepo.GetAppIdByPublicIdAsync(reportPublicId, ct);
        await RequireMembershipByAppIdAsync(appId, ct);
    }

    public async Task RequireMembershipByPagePublicIdAsync(Guid pagePublicId, CancellationToken ct = default)
    {
        if (_queryContext.IsSuperAdmin) return;
        var appId = await _pageRepo.GetAppIdByPublicIdAsync(pagePublicId, ct);
        await RequireMembershipByAppIdAsync(appId, ct);
    }

    private async Task RequireMembershipByAppIdAsync(long appId, CancellationToken ct)
    {
        var permissions = await _appUserRepo.GetUserAppPermissionsAsync(appId, _queryContext.UserId, ct);
        if (permissions.Count == 0)
            throw new UnauthorizedActionException("You are not a member of this app.");
    }
}
