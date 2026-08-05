using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Domain.Constants;

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

        var roleIds = await _appUserRepo.GetUserAppRoleIdsAsync(appId, _queryContext.UserId, ct);
        if (roleIds.Count == 0)
            roleIds = new[] { appUser.AppRoleId };

        var roleTablePerms = new Dictionary<long, IReadOnlyList<TablePermissionRow>>();
        var roleFieldPerms = new Dictionary<long, IReadOnlyList<FieldPermissionScopedRow>>();
        var roleFilters = new Dictionary<long, IReadOnlyList<RecordFilterRow>>();

        foreach (var rId in roleIds)
        {
            roleTablePerms[rId] = await _permRepo.GetTablePermissionsAsync(rId, ct);
            roleFieldPerms[rId] = await _permRepo.GetAllFieldPermissionsAsync(rId, ct);
            roleFilters[rId] = await _permRepo.GetRecordFiltersAsync(rId, ct);
        }

        // 1. Merge Table Permissions
        var allTablePublicIds = roleTablePerms.Values
            .SelectMany(list => list.Select(r => r.TablePublicId))
            .Distinct()
            .ToList();

        var mergedTablePerms = new List<AppGranularTablePermission>();
        foreach (var tablePublicId in allTablePublicIds)
        {
            var list = roleTablePerms.Values
                .SelectMany(perms => perms)
                .Where(r => r.TablePublicId == tablePublicId)
                .ToList();

            var canAdd = list.Any(r => r.CanAdd);
            var canDelete = list.Any(r => r.CanDelete);
            var canSaveSharedReports = list.Any(r => r.CanSaveSharedReports);
            var canEditFieldProperties = list.Any(r => r.CanEditFieldProperties);
            var viewScope = ResolveScope(list.Select(r => r.ViewScope));
            var modifyScope = ResolveScope(list.Select(r => r.ModifyScope));

            var hasFullAccess = list.Count < roleIds.Count || list.Any(r => r.FieldAccessLevel == TableFieldAccessLevels.FullAccess);
            var fieldAccessLevel = hasFullAccess ? TableFieldAccessLevels.FullAccess : TableFieldAccessLevels.CustomAccess;

            mergedTablePerms.Add(new AppGranularTablePermission(
                tablePublicId, viewScope, modifyScope, canAdd, canDelete,
                canSaveSharedReports, canEditFieldProperties, fieldAccessLevel));
        }

        // 2. Merge Field Permissions
        var mergedFieldPerms = new List<AppGranularFieldPermission>();
        var customTables = mergedTablePerms.Where(t => t.FieldAccessLevel == TableFieldAccessLevels.CustomAccess).ToList();

        foreach (var table in customTables)
        {
            var fieldPublicIds = roleFieldPerms.Values
                .SelectMany(perms => perms)
                .Where(fp => fp.TablePublicId == table.TablePublicId)
                .Select(fp => fp.FieldPublicId)
                .Distinct()
                .ToList();

            foreach (var fieldPublicId in fieldPublicIds)
            {
                var maxAccess = FieldAccessLevels.None;
                foreach (var rId in roleIds)
                {
                    var tablePerm = roleTablePerms[rId].FirstOrDefault(t => t.TablePublicId == table.TablePublicId);
                    if (tablePerm == null || tablePerm.FieldAccessLevel == TableFieldAccessLevels.FullAccess)
                    {
                        maxAccess = FieldAccessLevels.Modify;
                        break;
                    }

                    var fp = roleFieldPerms[rId].FirstOrDefault(f => f.TablePublicId == table.TablePublicId && f.FieldPublicId == fieldPublicId);
                    var access = fp?.Access ?? FieldAccessLevels.Modify;
                    maxAccess = GetHigherFieldAccess(maxAccess, access);
                }

                if (maxAccess != FieldAccessLevels.Modify)
                {
                    mergedFieldPerms.Add(new AppGranularFieldPermission(table.TablePublicId, fieldPublicId, maxAccess));
                }
            }
        }

        // 3. Merge Record Filters
        var mergedFilters = new List<AppGranularRecordFilter>();
        var filterGroups = roleFilters.Values
            .SelectMany(list => list)
            .GroupBy(r => r.TablePublicId);

        foreach (var group in filterGroups)
        {
            var list = group.ToList();
            foreach (var filterRow in list)
            {
                List<RoleRecordFilterCondition> conditions;
                try { conditions = string.IsNullOrWhiteSpace(filterRow.FilterJson) ? new() : JsonSerializer.Deserialize<List<RoleRecordFilterCondition>>(filterRow.FilterJson) ?? new(); }
                catch { conditions = new(); }
                if (conditions.Count > 0)
                {
                    mergedFilters.Add(new AppGranularRecordFilter(group.Key, filterRow.Conjunction, conditions));
                }
            }
        }

        return new AppPermissionsResult(roleName, permissions, mergedTablePerms, mergedFieldPerms, mergedFilters, _queryContext.UserId);
    }

    private static string ResolveScope(IEnumerable<string> scopes)
    {
        var scopeList = scopes.ToList();
        if (scopeList.Contains(RecordScopes.AllRecords)) return RecordScopes.AllRecords;
        if (scopeList.Contains(RecordScopes.OwnRecords)) return RecordScopes.OwnRecords;
        return RecordScopes.None;
    }

    private static string GetHigherFieldAccess(string a, string b)
    {
        if (a == FieldAccessLevels.Modify || b == FieldAccessLevels.Modify) return FieldAccessLevels.Modify;
        if (a == FieldAccessLevels.View || b == FieldAccessLevels.View) return FieldAccessLevels.View;
        return FieldAccessLevels.None;
    }
}
