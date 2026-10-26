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

        // ── Records the user may view / modify: see BuildViewAccessAsync / BuildModifyAccessAsync ──
        var view = await BuildViewAccessAsync(permissions, appUser, table, fields, ct);
        var modify = view.Scope == RecordScopes.None
            ? (Scope: RecordScopes.None, Filter: (FilterGroup?)null) // a record the user cannot see cannot be changed
            : await BuildModifyAccessAsync(permissions, table, fields, ct);
        if (view.Scope == RecordScopes.None) canDelete = false;

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

        return new TableAccessContext
        {
            Unrestricted = false,
            ViewScope = view.Scope,
            ModifyScope = modify.Scope,
            ModifyFilter = modify.Filter,
            CanAdd = canAdd,
            CanDelete = canDelete,
            VisibleFields = visibleFields,
            EditableFieldIds = editableFieldIds,
            ViewFilter = view.Filter,
            RestrictToCreatedBy = view.Scope == RecordScopes.OwnRecords ? _queryContext.UserId : null,
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

    private static string GetHigherFieldAccess(string a, string b)
    {
        if (a == FieldAccessLevels.Modify || b == FieldAccessLevels.Modify) return FieldAccessLevels.Modify;
        if (a == FieldAccessLevels.View || b == FieldAccessLevels.View) return FieldAccessLevels.View;
        return FieldAccessLevels.None;
    }

    /// <summary>
    /// Which records the user may view when they hold several roles. In order:
    /// 1. Explicit deny: a role whose View is "None" denies the table, whatever the other roles grant.
    /// 2. Allow: otherwise the grants add up (union). "All records" without Custom Rules grants every record,
    ///    "Own records" the records the user owns, "All records" with Custom Rules the records matching them.
    /// 3. No grant: Custom Rules that exist but cannot be used (malformed, every field deleted) grant nothing,
    ///    and when no role grants anything the user sees nothing.
    /// Custom Rules saved on a role whose View is "Own records" are ignored — that role grants its own records.
    /// </summary>
    private async Task<(string Scope, FilterGroup? Filter)> BuildViewAccessAsync(
        IReadOnlyList<AppRoleTablePermission> permissions, AppUser? appUser, AppTable table,
        IReadOnlyList<AppField> fields, CancellationToken ct)
    {
        if (permissions.Any(p => p.ViewScope == RecordScopes.None)) return (RecordScopes.None, null);

        var grants = new List<FilterGroup>();
        var ownRecords = false;
        foreach (var p in permissions)
        {
            if (p.ViewScope == RecordScopes.OwnRecords) { ownRecords = true; continue; }
            if (p.ViewScope != RecordScopes.AllRecords) continue;

            var stored = await _permRepo.GetRecordFilterAsync(p.AppRoleId, table.Id, ct);
            var rules = await BuildRoleViewRulesAsync(stored, appUser, table.AppId, fields, ct);
            if (rules.Unrestricted) return (RecordScopes.AllRecords, null);
            if (rules.Filter is not null) grants.Add(rules.Filter);
        }

        return CombineGrants(grants, ownRecords, fields);
    }

    /// <summary>
    /// One role's View Custom Rules. Unrestricted when it has none (or none with a condition); a null filter
    /// otherwise means the rules exist but cannot be used, so the role grants nothing.
    /// </summary>
    private async Task<(bool Unrestricted, FilterGroup? Filter)> BuildRoleViewRulesAsync(
        AppRoleRecordFilter? stored, AppUser? appUser, long appId, IReadOnlyList<AppField> fields, CancellationToken ct)
    {
        if (stored is null || string.IsNullOrWhiteSpace(stored.FilterJson)) return (true, null);

        if (RoleRecordFilterJson.IsGroupFormat(stored.FilterJson))
        {
            var parsed = RoleRecordFilterJson.ParseGroup(stored.FilterJson);
            if (parsed is null) return (false, null);
            if (RoleRecordFilterJson.CountConditions(parsed) == 0) return (true, null);
            return (false, await BuildGroupFilterAsync(stored.FilterJson, appId, fields, ct));
        }

        List<RoleRecordFilterCondition>? conditions;
        try { conditions = JsonSerializer.Deserialize<List<RoleRecordFilterCondition>>(stored.FilterJson); }
        catch { return (false, null); }
        if (conditions is null || conditions.Count == 0) return (true, null);

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
        if (nodes.Count == 0) return (false, null);

        return (false, new FilterGroup
        {
            Logic = stored.Conjunction.Equals("OR", StringComparison.OrdinalIgnoreCase) ? "or" : "and",
            Nodes = nodes,
        });
    }

    /// <summary>
    /// Which records the user may edit / delete, combined the same way as View (union), except that "None" is
    /// not a deny: it is every role's default, so it only grants nothing. Fails closed like View: Custom Rules
    /// that cannot be used grant nothing.
    /// </summary>
    private async Task<(string Scope, FilterGroup? Filter)> BuildModifyAccessAsync(
        IReadOnlyList<AppRoleTablePermission> permissions, AppTable table, IReadOnlyList<AppField> fields,
        CancellationToken ct)
    {
        var grants = new List<FilterGroup>();
        var ownRecords = false;
        foreach (var p in permissions)
        {
            if (p.ModifyScope == RecordScopes.OwnRecords) { ownRecords = true; continue; }
            if (p.ModifyScope != RecordScopes.AllRecords) continue;

            var stored = await _permRepo.GetModifyRecordFilterAsync(p.AppRoleId, table.Id, ct);
            if (stored is null || string.IsNullOrWhiteSpace(stored.FilterJson))
                return (RecordScopes.AllRecords, null); // this role may modify every record

            var parsed = RoleRecordFilterJson.ParseGroup(stored.FilterJson);
            if (parsed is not null && RoleRecordFilterJson.CountConditions(parsed) == 0)
                return (RecordScopes.AllRecords, null); // saved without a condition: no rules

            var group = parsed is null ? null : await BuildGroupFilterAsync(stored.FilterJson, table.AppId, fields, ct);
            if (group is not null) grants.Add(group);
        }

        return CombineGrants(grants, ownRecords, fields);
    }

    /// <summary>
    /// Union of the roles' Custom Rules plus, when a role grants "Own records", the records the user owns.
    /// Only own records -> scope OwnRecords (enforced by ownership, as for a single role); nothing -> None.
    /// </summary>
    private (string Scope, FilterGroup? Filter) CombineGrants(
        List<FilterGroup> grants, bool ownRecords, IReadOnlyList<AppField> fields)
    {
        if (grants.Count == 0)
            return (ownRecords ? RecordScopes.OwnRecords : RecordScopes.None, null);

        if (ownRecords)
        {
            var owner = fields.FirstOrDefault(f => f.IsSystem && f.PhysicalColumnName == "CreatedBy" && f.Fid.HasValue);
            if (owner is not null)
                grants.Add(new FilterGroup
                {
                    Logic = "and",
                    Nodes =
                    [
                        new FilterNode
                        {
                            Condition = new FilterCondition
                            {
                                FieldId = (long)owner.Fid!.Value, Operator = "eq", Value = _queryContext.UserId.ToString(),
                            },
                        },
                    ],
                });
        }

        if (grants.Count == 1) return (RecordScopes.AllRecords, grants[0]);
        return (RecordScopes.AllRecords, new FilterGroup
        {
            Logic = "or",
            Nodes = grants.Select(g => new FilterNode { Group = g }).ToList(),
        });
    }

    /// <summary>
    /// Builds the enforced filter from a stored nested <see cref="FilterGroup"/> (the report filter
    /// model). Resolved here, once per request, so every consumer of ViewFilter (reports, record
    /// lists, GetRecord, export, pipelines, imports) sees plain literal conditions — only the report
    /// path ever resolved relative dates / "current user", and a role filter must behave the same
    /// everywhere. Conditions on fields that no longer exist are dropped, as in the legacy path.
    /// </summary>
    private async Task<FilterGroup?> BuildGroupFilterAsync(
        string filterJson, long appId, IReadOnlyList<AppField> fields, CancellationToken ct)
    {
        var stored = RoleRecordFilterJson.ParseGroup(filterJson);
        if (stored is null) return null;

        var byFieldId = new Dictionary<long, AppField>();
        foreach (var f in fields) byFieldId[f.Fid.HasValue ? (long)f.Fid.Value : f.Id] = f;

        var pruned = PruneGroup(stored, byFieldId);
        if (pruned is null) return null;

        var resolved = RunReportQueryHandler.ResolveDateValueModeConditions(pruned);
        return await RunReportQueryHandler.ResolveUserFieldValuesAsync(
            resolved, byFieldId, _queryContext.UserId, new Dictionary<Guid, long>(), _userRepo, ct,
            appUserRepo: _appUserRepo, appId: appId,
            currentUserEmail: _queryContext.UserEmail, currentUserName: _queryContext.UserName);
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
