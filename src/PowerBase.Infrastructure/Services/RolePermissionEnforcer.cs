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
            return Unrestricted(fields); // not an app member via AppUser → leave existing access checks in charge

        var tablePerm = await _permRepo.GetTablePermissionAsync(appUser.AppRoleId, table.Id, ct)
                        ?? AppRoleTablePermission.Default(appUser.AppRoleId, table.Id);

        // ── Field visibility ──
        var hidden = new HashSet<long>();
        var viewOnly = new HashSet<long>();
        if (tablePerm.FieldAccessLevel == TableFieldAccessLevels.CustomAccess)
        {
            var accessMap = await _permRepo.GetFieldAccessMapAsync(appUser.AppRoleId, table.Id, ct);
            foreach (var (fieldId, access) in accessMap)
            {
                if (access == FieldAccessLevels.None) hidden.Add(fieldId);
                else if (access == FieldAccessLevels.View) viewOnly.Add(fieldId);
            }
        }

        var visibleFields = fields.Where(f => !hidden.Contains(f.Id)).ToList();
        var editableFieldIds = visibleFields
            .Where(f => !f.IsSystem && !viewOnly.Contains(f.Id))
            .Select(f => f.Id)
            .ToHashSet();

        // ── Record filter (role-defined conditions) ──
        var viewFilter = await BuildViewFilterAsync(appUser, table, fields, ct);

        return new TableAccessContext
        {
            Unrestricted = false,
            ViewScope = tablePerm.ViewScope,
            ModifyScope = tablePerm.ModifyScope,
            CanAdd = tablePerm.CanAdd,
            CanDelete = tablePerm.CanDelete,
            VisibleFields = visibleFields,
            EditableFieldIds = editableFieldIds,
            ViewFilter = viewFilter,
            RestrictToCreatedBy = tablePerm.ViewScope == RecordScopes.OwnRecords ? _queryContext.UserId : null,
        };
    }

    public async Task EnsureRecordOwnedAsync(AppTable table, Guid recordPublicId, CancellationToken ct = default)
    {
        // Selecting with no custom fields still returns the CreatedBy system column.
        var row = await _recordRepo.GetByPublicIdAsync(table, Array.Empty<AppField>(), recordPublicId, ct);
        if (!row.TryGetValue("CreatedBy", out var createdBy) || Convert.ToInt64(createdBy) != _queryContext.UserId)
            throw new UnauthorizedActionException("You can only modify records you created.");
    }

    private async Task<FilterGroup?> BuildViewFilterAsync(AppUser appUser, AppTable table, IReadOnlyList<AppField> fields, CancellationToken ct)
    {
        var stored = await _permRepo.GetRecordFilterAsync(appUser.AppRoleId, table.Id, ct);
        if (stored is null || string.IsNullOrWhiteSpace(stored.FilterJson)) return null;

        List<RoleRecordFilterCondition>? conditions;
        try { conditions = JsonSerializer.Deserialize<List<RoleRecordFilterCondition>>(stored.FilterJson); }
        catch { return null; }
        if (conditions is null || conditions.Count == 0) return null;

        var byPublicId = fields.ToDictionary(f => f.PublicId);
        var nodes = new List<FilterNode>();
        foreach (var c in conditions)
        {
            if (!byPublicId.TryGetValue(c.FieldPublicId, out var field)) continue;
            var value = c.UseCurrentUser ? appUser.UserPublicId?.ToString() : c.Value;
            nodes.Add(new FilterNode
            {
                Condition = new FilterCondition { FieldId = field.Id, Operator = c.Operator, Value = value },
            });
        }
        if (nodes.Count == 0) return null;

        return new FilterGroup
        {
            Logic = stored.Conjunction.Equals("OR", StringComparison.OrdinalIgnoreCase) ? "or" : "and",
            Nodes = nodes,
        };
    }

    private static TableAccessContext Unrestricted(IReadOnlyList<AppField> fields) => new()
    {
        Unrestricted = true,
        VisibleFields = fields,
        EditableFieldIds = fields.Where(f => !f.IsSystem).Select(f => f.Id).ToHashSet(),
    };
}
