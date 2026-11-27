using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
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
    private readonly IAppRoleRepository? _appRoleRepo;
    private readonly IRelationshipRepository _relRepo;
    private readonly IAppRolePermissionRepository? _permRepo;

    public AppAccessService(
        IAppRepository appRepo,
        IAppTableRepository tableRepo,
        IReportRepository reportRepo,
        IFormRepository formRepo,
        IFormRuleRepository formRuleRepo,
        IPageRepository pageRepo,
        IAppUserRepository appUserRepo,
        IQueryContext queryContext,
        IPipelineRepository pipelineRepo,
        IRelationshipRepository relRepo,
        IAppRoleRepository? appRoleRepo = null,
        IAppRolePermissionRepository? permRepo = null)
    {
        _permRepo = permRepo;
        _appRepo = appRepo;
        _tableRepo = tableRepo;
        _reportRepo = reportRepo;
        _formRepo = formRepo;
        _formRuleRepo = formRuleRepo;
        _pageRepo = pageRepo;
        _appUserRepo = appUserRepo;
        _queryContext = queryContext;
        _pipelineRepo = pipelineRepo;
        _relRepo = relRepo;
        _appRoleRepo = appRoleRepo;
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
        if (IsTableScoped(permissionCode))
        {
            var table = await _tableRepo.GetByPublicIdAsync(tablePublicId, ct);
            await RequireTablePermissionAsync(appId, table.Id, permissionCode, ct);
        }
    }

    public async Task RequirePermissionByReportPublicIdAsync(Guid reportPublicId, string permissionCode, CancellationToken ct = default)
    {
        var appId = await _reportRepo.GetAppIdByPublicIdAsync(reportPublicId, ct);
        await RequirePermissionByAppIdAsync(appId, permissionCode, ct);
        if (IsTableScoped(permissionCode) && await _reportRepo.GetAppTableIdByPublicIdAsync(reportPublicId, ct) is long tableId)
            await RequireTablePermissionAsync(appId, tableId, permissionCode, ct);
    }

    public async Task RequirePermissionByFormPublicIdAsync(Guid formPublicId, string permissionCode, CancellationToken ct = default)
    {
        var appId = await _formRepo.GetAppIdByPublicIdAsync(formPublicId, ct);
        await RequirePermissionByAppIdAsync(appId, permissionCode, ct);
        if (IsTableScoped(permissionCode))
        {
            var form = await _formRepo.GetByPublicIdAsync(formPublicId, ct);
            await RequireTablePermissionAsync(appId, form.AppTableId, permissionCode, ct);
        }
    }

    public async Task RequirePermissionByFormRulePublicIdAsync(Guid rulePublicId, string permissionCode, CancellationToken ct = default)
    {
        var appId = await _formRuleRepo.GetAppIdByPublicIdAsync(rulePublicId, ct);
        await RequirePermissionByAppIdAsync(appId, permissionCode, ct);
        if (IsTableScoped(permissionCode))
        {
            var rule = await _formRuleRepo.GetByPublicIdAsync(rulePublicId, ct);
            if (await _formRepo.GetTableIdByFormIdAsync(rule.FormId, ct) is long tableId)
                await RequireTablePermissionAsync(appId, tableId, permissionCode, ct);
        }
    }

    /// <summary>Builder permissions a role can narrow per table (Roles > Advanced Permissions). Super admins are not
    /// narrowed. Create / Update / Delete / Form Rules always; the *:read ("View") codes only for builder screens and
    /// direct API calls (see PageContext).</summary>
    private bool IsTableScoped(string permissionCode)
    {
        if (_permRepo is null || _queryContext.IsSuperAdmin) return false;
        if (PermissionCodes.TableScopedEnforced.Contains(permissionCode)) return true;
        // Per-table "View" (tables / fields / reports / forms :read) guards an item's definition on builder screens and for
        // direct API calls; data pages load the same definitions and are governed by Data Permissions (PageContext).
        return TableViewCodes.Contains(permissionCode)
            && PowerBase.Application.Common.PageContext.EnforcesTableView(permissionCode, _queryContext.ApiReadPath, _queryContext.ClientPage);
    }

    private static readonly HashSet<string> TableViewCodes =
    [
        PermissionCodes.TablesRead, PermissionCodes.FieldsRead, PermissionCodes.ReportsRead, PermissionCodes.FormsRead,
    ];

    /// <summary>The user holds the permission on this table: one of their roles has it and does not exclude the table.</summary>
    private async Task RequireTablePermissionAsync(long appId, long tableId, string permissionCode, CancellationToken ct)
    {
        var roleIds = await _appUserRepo.GetUserAppRoleIdsAsync(appId, _queryContext.UserId, ct);
        if (!await _permRepo!.HasTablePermissionAsync(roleIds, permissionCode, tableId, ct))
            throw new UnauthorizedActionException("Your role does not have this permission on this table.");
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

    public async Task RequirePermissionByRolePublicIdAsync(Guid rolePublicId, string permissionCode, CancellationToken ct = default)
    {
        if (_appRoleRepo == null)
        {
            throw new InvalidOperationException("IAppRoleRepository is not configured.");
        }

        var role = await _appRoleRepo.GetByPublicIdAsync(rolePublicId, ct)
            ?? throw new NotFoundException("AppRole", rolePublicId);

        await RequirePermissionByAppIdAsync(role.AppId, permissionCode, ct);
    }

    public async Task RequirePermissionByRelationshipPublicIdAsync(Guid relationshipPublicId, string permissionCode, CancellationToken ct = default)
    {
        var rel = await _relRepo.GetByPublicIdAsync(relationshipPublicId, ct)
            ?? throw new NotFoundException("Relationship", relationshipPublicId);
        await RequirePermissionByAppIdAsync(rel.AppId, permissionCode, ct);
    }

    public async Task RequirePermissionByAppIdAsync(long appId, string permissionCode, CancellationToken ct = default)
    {
        EnsureTokenAppAccess(appId);
        var permissions = await _appUserRepo.GetUserAppPermissionsAsync(appId, _queryContext.UserId, ct);
        if (!permissions.Contains(permissionCode))
        {
            throw new UnauthorizedActionException("You do not have permission to perform this action in this app.");
        }
    }

    public async Task RequireAppRoleAsync(Guid appPublicId, string roleName, CancellationToken ct = default)
    {
        var appId = await _appRepo.GetIdByPublicIdAsync(appPublicId, ct);
        EnsureTokenAppAccess(appId);
        var actualRoleName = await _appUserRepo.GetUserRoleNameAsync(appId, _queryContext.UserId, ct);

        if (actualRoleName != roleName)
        {
            throw new UnauthorizedActionException($"This action requires the '{roleName}' app role.");
        }
    }

    public async Task RequireMembershipByTablePublicIdAsync(Guid tablePublicId, CancellationToken ct = default)
    {
        var appId = await _tableRepo.GetAppIdByPublicIdAsync(tablePublicId, ct);
        EnsureTokenAppAccess(appId);
        if (_queryContext.IsSuperAdmin) return;
        await RequireMembershipByAppIdAsync(appId, ct);
    }

    public async Task RequireMembershipByReportPublicIdAsync(Guid reportPublicId, CancellationToken ct = default)
    {
        var appId = await _reportRepo.GetAppIdByPublicIdAsync(reportPublicId, ct);
        EnsureTokenAppAccess(appId);
        if (_queryContext.IsSuperAdmin) return;
        await RequireMembershipByAppIdAsync(appId, ct);
    }

    public async Task RequireMembershipByPagePublicIdAsync(Guid pagePublicId, CancellationToken ct = default)
    {
        var appId = await _pageRepo.GetAppIdByPublicIdAsync(pagePublicId, ct);
        EnsureTokenAppAccess(appId);
        if (_queryContext.IsSuperAdmin) return;
        await RequireMembershipByAppIdAsync(appId, ct);
    }

    public async Task RequireMembershipByAppPublicIdAsync(Guid appPublicId, CancellationToken ct = default)
    {
        var appId = await _appRepo.GetIdByPublicIdAsync(appPublicId, ct);
        EnsureTokenAppAccess(appId);
        if (_queryContext.IsSuperAdmin) return;
        await RequireMembershipByAppIdAsync(appId, ct);
    }

    public async Task RequireCanAddRecordsByTablePublicIdAsync(Guid tablePublicId, CancellationToken ct = default)
    {
        await RequireMembershipByTablePublicIdAsync(tablePublicId, ct);
        if (_queryContext.IsSuperAdmin) return;

        var table = await _tableRepo.GetByPublicIdAsync(tablePublicId, ct);
        var roleIds = await _appUserRepo.GetUserAppRoleIdsAsync(table.AppId, _queryContext.UserId, ct);
        if (_permRepo is not null)
        {
            foreach (var roleId in roleIds)
            {
                // A role with no row for the table gets the default, which does not allow adding (AppRoleTablePermission.Default).
                var perm = await _permRepo.GetTablePermissionAsync(roleId, table.Id, ct);
                if (perm?.CanAdd == true) return;
            }
        }
        throw new UnauthorizedActionException("You do not have permission to add records to this table.");
    }

    private async Task RequireMembershipByAppIdAsync(long appId, CancellationToken ct)
    {
        EnsureTokenAppAccess(appId);
        var permissions = await _appUserRepo.GetUserAppPermissionsAsync(appId, _queryContext.UserId, ct);
        if (permissions.Count == 0)
            throw new UnauthorizedActionException("You are not a member of this app.");
    }

    private void EnsureTokenAppAccess(long appId)
    {
        if (_queryContext.IsUserToken && !_queryContext.TokenAccessAllApps && !_queryContext.AllowedAppIds.Contains(appId))
        {
            throw new UnauthorizedActionException("This user token does not have access to this application.");
        }
    }
}
