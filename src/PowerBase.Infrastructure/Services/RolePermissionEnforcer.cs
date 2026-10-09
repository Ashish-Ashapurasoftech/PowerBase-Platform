using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Queries.RunReport;
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
    private readonly IUserRepository _userRepo;

    public RolePermissionEnforcer(
        IQueryContext queryContext,
        IAppUserRepository appUserRepo,
        IAppRolePermissionRepository permRepo,
        IRecordRepository recordRepo,
        IUserRepository userRepo)
    {
        _queryContext = queryContext;
        _appUserRepo = appUserRepo;
        _permRepo = permRepo;
        _recordRepo = recordRepo;
        _userRepo = userRepo;
    }

    public async Task<TableAccessContext> GetTableAccessAsync(AppTable table, IReadOnlyList<AppField> fields, CancellationToken ct = default)
    {
        // Super admins bypass granular enforcement entirely.
        if (_queryContext.IsSuperAdmin)
            return Unrestricted(fields);

        var roleIds = await _appUserRepo.GetUserAppRoleIdsAsync(table.AppId, _queryContext.UserId, ct);
        if (roleIds.Count == 0)
            return Unrestricted(fields); // not an app member

        var appUser = await _appUserRepo.GetByAppAndUserAsync(table.AppId, _queryContext.UserId, ct);

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
        var row = await _recordRepo.GetByPublicIdAsync(table, Array.Empty<AppField>(), recordPublicId, ct: ct);
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
        IReadOnlyList<long> roleIds, AppUser? appUser, AppTable table, IReadOnlyList<AppField> fields, CancellationToken ct)
    {
        var childGroups = new List<FilterGroup>();
        foreach (var rId in roleIds)
        {
            var stored = await _permRepo.GetRecordFilterAsync(rId, table.Id, ct);
            if (stored is null || string.IsNullOrWhiteSpace(stored.FilterJson)) continue;

            if (RoleRecordFilterJson.IsGroupFormat(stored.FilterJson))
            {
                var treeFilter = await BuildGroupFilterAsync(stored.FilterJson, fields, ct);
                if (treeFilter is not null) childGroups.Add(treeFilter);
                continue;
            }

            List<RoleRecordFilterCondition>? conditions;
            try { conditions = JsonSerializer.Deserialize<List<RoleRecordFilterCondition>>(stored.FilterJson); }
            catch { continue; }
            if (conditions is null || conditions.Count == 0) continue;

            var byPublicId = fields.ToDictionary(f => f.PublicId);
            var nodes = new List<FilterNode>();
            foreach (var c in conditions)
            {
                if (!byPublicId.TryGetValue(c.FieldPublicId, out var field)) continue;
                var value = c.UseCurrentUser ? (appUser?.UserPublicId?.ToString() ?? string.Empty) : c.Value;
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

    /// <summary>
    /// Builds the enforced filter from a stored nested <see cref="FilterGroup"/> (the report filter
    /// model). Resolved here, once per request, so every consumer of ViewFilter (reports, record
    /// lists, GetRecord, export, pipelines, imports) sees plain literal conditions — only the report
    /// path ever resolved relative dates / "current user", and a role filter must behave the same
    /// everywhere. Conditions on fields that no longer exist are dropped, as in the legacy path.
    /// </summary>
    private async Task<FilterGroup?> BuildGroupFilterAsync(
        string filterJson, IReadOnlyList<AppField> fields, CancellationToken ct)
    {
        var stored = RoleRecordFilterJson.ParseGroup(filterJson);
        if (stored is null) return null;

        var byFieldId = new Dictionary<long, AppField>();
        foreach (var f in fields) byFieldId[f.Fid.HasValue ? (long)f.Fid.Value : f.Id] = f;

        var pruned = PruneGroup(stored, byFieldId);
        if (pruned is null) return null;

        var resolved = RunReportQueryHandler.ResolveDateValueModeConditions(pruned);
        return await RunReportQueryHandler.ResolveUserFieldValuesAsync(
            resolved, byFieldId, _queryContext.UserId, new Dictionary<Guid, long>(), _userRepo, ct);
    }

    /// <summary>Drops unusable conditions ("ask the user" / parent-field tiers have no meaning for a
    /// role filter; unknown fields) and any group left empty. Null when nothing remains.</summary>
    private static FilterGroup? PruneGroup(FilterGroup group, IReadOnlyDictionary<long, AppField> byFieldId)
    {
        var nodes = new List<FilterNode>();
        foreach (var n in group.Nodes)
        {
            if (n.Condition is { } c)
            {
                if (!byFieldId.ContainsKey(c.FieldId)) continue;
                if (string.Equals(c.ValueMode, "ask", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(c.ValueMode, "parentField", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(c.ValueMode, "field", StringComparison.OrdinalIgnoreCase)
                    && !(c.ValueFieldId.HasValue && byFieldId.ContainsKey(c.ValueFieldId.Value))) continue;
                nodes.Add(new FilterNode { Condition = c });
            }
            else if (n.Group is { } g)
            {
                var child = PruneGroup(g, byFieldId);
                if (child is not null) nodes.Add(new FilterNode { Group = child });
            }
        }
        return nodes.Count == 0 ? null : new FilterGroup { Logic = group.Logic, Nodes = nodes };
    }

    private static TableAccessContext Unrestricted(IReadOnlyList<AppField> fields) => new()
    {
        Unrestricted = true,
        VisibleFields = fields,
        EditableFieldIds = fields.Where(f => !f.IsSystem).Select(f => f.Id).ToHashSet(),
    };
}
