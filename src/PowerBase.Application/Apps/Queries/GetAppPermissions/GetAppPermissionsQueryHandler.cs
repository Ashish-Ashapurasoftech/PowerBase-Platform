using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;

namespace PowerBase.Application.Apps.Queries.GetAppPermissions;

public record AppGranularTablePermission(
    Guid TablePublicId, string ViewScope, string ModifyScope,
    bool CanAdd, bool CanDelete, bool CanSaveSharedReports, bool CanEditFieldProperties,
    string FieldAccessLevel);

public record AppGranularFieldPermission(Guid TablePublicId, Guid FieldPublicId, string Access);

public record AppGranularRecordFilter(
    Guid TablePublicId, string Conjunction, IReadOnlyList<RoleRecordFilterCondition> Conditions);

public record AppPermissionsResult(
    string? RoleName,
    IReadOnlySet<string> Permissions,
    IReadOnlyList<AppGranularTablePermission> TablePermissions,
    IReadOnlyList<AppGranularFieldPermission> FieldPermissions,
    IReadOnlyList<AppGranularRecordFilter> RecordFilters,
    /// <summary>The current user's internal userId — used by the UI for per-row OwnRecords gating.</summary>
    long CurrentUserId = 0);

public class GetAppPermissionsQueryHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppUserRepository _appUserRepo;
    private readonly IAppRolePermissionRepository _permRepo;
    private readonly IQueryContext _queryContext;

    public GetAppPermissionsQueryHandler(
        IAppRepository appRepo,
        IAppUserRepository appUserRepo,
        IAppRolePermissionRepository permRepo,
        IQueryContext queryContext)
    {
        _appRepo = appRepo;
        _appUserRepo = appUserRepo;
        _permRepo = permRepo;
        _queryContext = queryContext;
    }

    public async Task<AppPermissionsResult> HandleAsync(GetAppPermissionsQuery query, CancellationToken ct = default)
    {
        var appId = await _appRepo.GetIdByPublicIdAsync(query.AppPublicId, ct);
        var permissions = await _appUserRepo.GetUserAppPermissionsAsync(appId, _queryContext.UserId, ct);
        var roleName = await _appUserRepo.GetUserRoleNameAsync(appId, _queryContext.UserId, ct);

        var appUser = await _appUserRepo.GetByAppAndUserAsync(appId, _queryContext.UserId, ct);
        if (appUser is null)
            return new AppPermissionsResult(roleName, permissions, [], [], [], _queryContext.UserId);

        var tableRows = await _permRepo.GetTablePermissionsAsync(appUser.AppRoleId, ct);
        var fieldRows = await _permRepo.GetAllFieldPermissionsAsync(appUser.AppRoleId, ct);
        var filterRows = await _permRepo.GetRecordFiltersAsync(appUser.AppRoleId, ct);

        var tablePermissions = tableRows.Select(r => new AppGranularTablePermission(
            r.TablePublicId, r.ViewScope, r.ModifyScope, r.CanAdd, r.CanDelete,
            r.CanSaveSharedReports, r.CanEditFieldProperties, r.FieldAccessLevel)).ToList();

        var fieldPermissions = fieldRows.Select(r =>
            new AppGranularFieldPermission(r.TablePublicId, r.FieldPublicId, r.Access)).ToList();

        var recordFilters = filterRows.Select(r =>
        {
            List<RoleRecordFilterCondition> conditions;
            try { conditions = string.IsNullOrWhiteSpace(r.FilterJson) ? new() : JsonSerializer.Deserialize<List<RoleRecordFilterCondition>>(r.FilterJson) ?? new(); }
            catch { conditions = new(); }
            return new AppGranularRecordFilter(r.TablePublicId, r.Conjunction, conditions);
        }).ToList();

        return new AppPermissionsResult(roleName, permissions, tablePermissions, fieldPermissions, recordFilters, _queryContext.UserId);
    }
}
