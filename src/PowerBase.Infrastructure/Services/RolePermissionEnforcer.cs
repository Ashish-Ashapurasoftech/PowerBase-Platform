using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Reports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Infrastructure.Services;

public class RolePermissionEnforcer : IRolePermissionEnforcer
{
    private readonly IQueryContext _queryContext;
    private readonly IAppUserRepository _appUserRepo;
    private readonly IAppRolePermissionRepository _permRepo;
    private readonly IRecordRepository _recordRepo;

    public RolePermissionEnforcer(
        IQueryContext queryContext,
        IAppUserRepository appUserRepo,
        IAppRolePermissionRepository permRepo,
        IRecordRepository recordRepo)
    {
        _queryContext = queryContext;
        _appUserRepo = appUserRepo;
        _permRepo = permRepo;
        _recordRepo = recordRepo;
    }

    public async Task<TableAccessContext> GetTableAccessAsync(AppTable table, IReadOnlyList<AppField> fields, CancellationToken ct = default)
    {
        // Super admins bypass granular enforcement entirely.
        if (_queryContext.IsSuperAdmin)
            return Unrestricted(fields);

        var appUser = await _appUserRepo.GetByAppAndUserAsync(table.AppId, _queryContext.UserId, ct);
        if (appUser is null)
            return Unrestricted(fields); // not an app member via AppUser

        var roleIds = await _appUserRepo.GetUserAppRoleIdsAsync(table.AppId, _queryContext.UserId, ct);
        if (roleIds.Count == 0)
            roleIds = new[] { appUser.AppRoleId };

        var permissions = new List<AppRoleTablePermission>();
        foreach (var rId in roleIds)
        {
            var p = await _permRepo.GetTablePermissionAsync(rId, table.Id, ct)
                    ?? AppRoleTablePermission.Default(rId, table.Id);
            permissions.Add(p);
        }

        var canAdd = permissions.Any(p => p.CanAdd);
        var canDelete = permissions.Any(p => p.CanDelete);

        var viewScope = ResolveScope(permissions.Select(p => p.ViewScope));
        var modifyScope = ResolveScope(permissions.Select(p => p.ModifyScope));

        // ── Field visibility ──
        var hidden = new HashSet<long>();
        var viewOnly = new HashSet<long>();

        var fieldMaxAccess = new Dictionary<long, string>();
        foreach (var f in fields)
        {
            fieldMaxAccess[f.Id] = FieldAccessLevels.None;
        }

        foreach (var rId in roleIds)
        {
            var perm = permissions.First(p => p.AppRoleId == rId);
            if (perm.FieldAccessLevel == TableFieldAccessLevels.FullAccess)
            {
                foreach (var f in fields)
                {
                    fieldMaxAccess[f.Id] = FieldAccessLevels.Modify;
                }
            }
            else
            {
                var accessMap = await _permRepo.GetFieldAccessMapAsync(rId, table.Id, ct);
                foreach (var f in fields)
                {
                    var level = accessMap.TryGetValue(f.Id, out var val) ? val : FieldAccessLevels.Modify;
                    var currentMax = fieldMaxAccess.GetValueOrDefault(f.Id, FieldAccessLevels.None);
                    fieldMaxAccess[f.Id] = GetHigherFieldAccess(currentMax, level);
                }
            }
        }

        foreach (var f in fields)
        {
            var maxAccess = fieldMaxAccess.GetValueOrDefault(f.Id, FieldAccessLevels.Modify);
            if (maxAccess == FieldAccessLevels.None)
                hidden.Add(f.Id);
            else if (maxAccess == FieldAccessLevels.View)
                viewOnly.Add(f.Id);
        }

        var visibleFields = fields.Where(f => !hidden.Contains(f.Id)).ToList();
        var editableFieldIds = visibleFields
            .Where(f => !f.IsSystem && !viewOnly.Contains(f.Id) && f.Fid.HasValue)
            .Select(f => (long)f.Fid!.Value)
            .ToHashSet();

        // ── Record filter (role-defined conditions) ──
        var viewFilter = await BuildCombinedViewFilterAsync(roleIds, appUser, table, fields, ct);

        return new TableAccessContext
        {
            Unrestricted = false,
            ViewScope = viewScope,
            ModifyScope = modifyScope,
            CanAdd = canAdd,
            CanDelete = canDelete,
            VisibleFields = visibleFields,
            EditableFieldIds = editableFieldIds,
            ViewFilter = viewFilter,
            RestrictToCreatedBy = viewScope == RecordScopes.OwnRecords ? _queryContext.UserId : null,
        };
    }

    public async Task EnsureRecordOwnedAsync(AppTable table, Guid recordPublicId, CancellationToken ct = default)
    {
        var row = await _recordRepo.GetByPublicIdAsync(table, Array.Empty<AppField>(), recordPublicId, ct);
        if (!row.TryGetValue("CreatedBy", out var createdBy) || Convert.ToInt64(createdBy) != _queryContext.UserId)
            throw new UnauthorizedActionException("You can only modify records you created.");
    }

    public async Task EnsureButtonWriteAllowedAsync(
        AppTable table, IReadOnlyList<AppField> fields, Guid recordPublicId,
        IReadOnlySet<long> buttonTargetFids, CancellationToken ct = default)
    {
        if (buttonTargetFids.Count == 0)
            throw new UnauthorizedActionException("This button has no configured fields to write.");

        var access = await GetTableAccessAsync(table, fields, ct);
        if (access.Unrestricted)
            return;

        if (!access.CanView)
            throw new UnauthorizedActionException("You do not have permission to view this record.");

        if (access.ViewScope == RecordScopes.OwnRecords || access.ModifyScope == RecordScopes.OwnRecords)
            await EnsureRecordOwnedAsync(table, recordPublicId, ct);
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

    private async Task<FilterGroup?> BuildCombinedViewFilterAsync(
        IReadOnlyList<long> roleIds, AppUser appUser, AppTable table, IReadOnlyList<AppField> fields, CancellationToken ct)
    {
        var childGroups = new List<FilterGroup>();
        foreach (var rId in roleIds)
        {
            var stored = await _permRepo.GetRecordFilterAsync(rId, table.Id, ct);
            if (stored is null || string.IsNullOrWhiteSpace(stored.FilterJson)) continue;

            List<RoleRecordFilterCondition>? conditions;
            try { conditions = JsonSerializer.Deserialize<List<RoleRecordFilterCondition>>(stored.FilterJson); }
            catch { continue; }
            if (conditions is null || conditions.Count == 0) continue;

            var byPublicId = fields.ToDictionary(f => f.PublicId);
            var nodes = new List<FilterNode>();
            foreach (var c in conditions)
            {
                if (!byPublicId.TryGetValue(c.FieldPublicId, out var field)) continue;
                var value = c.UseCurrentUser ? appUser.UserPublicId?.ToString() : c.Value;
                var fieldId = field.Fid.HasValue ? (long)field.Fid.Value : field.Id;
                nodes.Add(new FilterNode
                {
                    Condition = new FilterCondition { FieldId = fieldId, Operator = c.Operator, Value = value },
                });
            }
            if (nodes.Count == 0) continue;

            childGroups.Add(new FilterGroup
            {
                Logic = stored.Conjunction.Equals("OR", StringComparison.OrdinalIgnoreCase) ? "or" : "and",
                Nodes = nodes,
            });
        }

        if (childGroups.Count == 0) return null;
        if (childGroups.Count == 1) return childGroups[0];

        return new FilterGroup
        {
            Logic = "or",
            Nodes = childGroups.Select(cg => new FilterNode { Group = cg }).ToList()
        };
    }

    private static TableAccessContext Unrestricted(IReadOnlyList<AppField> fields) => new()
    {
        Unrestricted = true,
        VisibleFields = fields,
        EditableFieldIds = fields.Where(f => !f.IsSystem).Select(f => f.Id).ToHashSet(),
    };
}
