using PowerBase.Application.Apps.Commands.UpdateRecordFilters;
using PowerBase.Application.Apps.Commands.UpdateTablePermissions;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Import.Commands.ImportAppFromPbl;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Reports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Import.Services;

/// <summary>
/// The role settings that need the finished schema: functional permission codes, record filters and field-level access.
/// Runs after every field exists. Security rules:
///   • Built-in roles (Administrator / Participant / Viewer) are never given extra restrictions or permission changes here — the
///     importing user is an Administrator and must not be locked out by the import.
///   • A restriction that cannot be stored is never dropped silently: the role loses access to that table instead
///     (least privilege), and the reason is reported.
/// </summary>
public class PblPermissionsApplier
{
    private readonly IAppRepository _appRepo;
    private readonly IAppRoleRepository _roleRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IAppRolePermissionRepository _permRepo;
    private readonly UpdateRecordFiltersCommandHandler _recordFiltersHandler;
    private readonly UpdateTablePermissionsCommandHandler _tablePermissionsHandler;

    public PblPermissionsApplier(
        IAppRepository appRepo, IAppRoleRepository roleRepo, IAppTableRepository tableRepo, IAppFieldRepository fieldRepo,
        IAppRolePermissionRepository permRepo, UpdateRecordFiltersCommandHandler recordFiltersHandler,
        UpdateTablePermissionsCommandHandler tablePermissionsHandler)
    {
        _appRepo = appRepo;
        _roleRepo = roleRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _permRepo = permRepo;
        _recordFiltersHandler = recordFiltersHandler;
        _tablePermissionsHandler = tablePermissionsHandler;
    }

    // Functional permission bundles. A new role starts with the structural reads CreateAppRole gives every role.
    private static readonly string[] BaseCodes =
    [
        PermissionCodes.TablesRead, PermissionCodes.FieldsRead, PermissionCodes.ReportsRead,
        PermissionCodes.ReportsRun, PermissionCodes.FormsRead, PermissionCodes.PowerFlowsRead,
    ];

    private static readonly string[] ManageUsersCodes = [PermissionCodes.UsersManage, PermissionCodes.UsersInvite];

    private static readonly string[] EditAppCodes =
    [
        PermissionCodes.AppsUpdate, PermissionCodes.RolesManage,
        PermissionCodes.TablesCreate, PermissionCodes.TablesUpdate, PermissionCodes.TablesDelete,
        PermissionCodes.FieldsCreate, PermissionCodes.FieldsUpdate, PermissionCodes.FieldsDelete,
        PermissionCodes.FormsCreate, PermissionCodes.FormsUpdate, PermissionCodes.FormsDelete, PermissionCodes.FormsRulesManage,
        PermissionCodes.ReportsCreate, PermissionCodes.ReportsUpdate, PermissionCodes.ReportsDelete,
        PermissionCodes.PagesCreate, PermissionCodes.PagesRead, PermissionCodes.PagesUpdate, PermissionCodes.PagesDelete, PermissionCodes.PagesPublish,
    ];

    /// <summary>The permission codes for an imported role: none at all when access is disabled, otherwise the base reads plus the
    /// bundles the source role's switches ask for.</summary>
    public static IReadOnlyList<string> PermissionCodesFor(PblRole role)
    {
        if (role.DisableAccess)
            return [];

        var codes = new List<string>(BaseCodes);
        if (role.ManageUsers) codes.AddRange(ManageUsersCodes);
        if (role.EditApp) codes.AddRange(EditAppCodes);
        return codes.Distinct().ToList();
    }

    public virtual async Task ApplyAsync(PblDocument document, Guid appPublicId, List<ImportSkippedItem> skipped, CancellationToken ct)
    {
        var roles = document.Roles ?? [];
        if (roles.Count == 0)
            return;

        var appId = await _appRepo.GetIdByPublicIdAsync(appPublicId, ct);
        var roleByName = (await _roleRepo.ListDetailsByAppIdAsync(appId, ct)).ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        var tableByName = (await _tableRepo.ListByAppAsync(appId, ct)).ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var tableNameByRef = (document.Tables ?? []).ToDictionary(t => t.LogicalRef, t => t.Name, StringComparer.Ordinal);
        var fieldCache = new Dictionary<long, IReadOnlyList<AppField>>();

        foreach (var role in roles)
        {
            if (!roleByName.TryGetValue(role.Name, out var created))
                continue;

            if (created.IsSystem)
            {
                if (role.TablePermissions.Any(p => p.RecordFilter is not null || p.FieldPermissions.Count > 0) || role.DisableAccess || role.EditApp || role.ManageUsers)
                    skipped.Add(new ImportSkippedItem { LogicalRef = role.LogicalRef, Name = role.Name,
                        Reason = $"'{role.Name}' is a built-in role: its permission set, record filters and field access were not changed by the import." });
                continue;
            }

            try
            {
                await _roleRepo.SetPermissionsAsync(created.Id, PermissionCodesFor(role), null, ct);
            }
            catch (DomainException ex)
            {
                skipped.Add(new ImportSkippedItem { LogicalRef = role.LogicalRef, Name = role.Name, Reason = $"Permissions not applied: {Describe(ex)}" });
            }

            if (role.RestrictAccessToNewTables)
                skipped.Add(new ImportSkippedItem { LogicalRef = role.LogicalRef, Name = role.Name,
                    Reason = "'Restrict access to new tables' has no PowerBase setting; tables created later follow PowerBase's own defaults for this role." });

            var filterInputs = new List<RecordFilterInput>();
            var failedTables = new List<(AppTable Table, string Reason)>();

            foreach (var permission in role.TablePermissions)
            {
                if (!tableNameByRef.TryGetValue(permission.TableRef, out var tableName) || !tableByName.TryGetValue(tableName, out var table))
                    continue;

                if (!fieldCache.TryGetValue(table.Id, out var fields))
                    fieldCache[table.Id] = fields = await _fieldRepo.ListByTableAsync(table.Id, ct);

                // ---- record filter ----
                if (permission.RecordFilter is not null)
                {
                    var group = ToFilterGroup(permission.RecordFilter, fields, out var problem);
                    if (group is null)
                        failedTables.Add((table, problem ?? "filter could not be built"));
                    else
                        filterInputs.Add(new RecordFilterInput(table.PublicId, "AND", [], group));
                }

                // ---- field-level access ----
                if (permission.FieldPermissions.Count > 0)
                {
                    var rows = new List<AppRoleFieldPermission>();
                    var byName = FieldIndex(fields);
                    foreach (var fp in permission.FieldPermissions)
                    {
                        if (!byName.TryGetValue(fp.FieldName, out var field))
                        {
                            skipped.Add(new ImportSkippedItem { LogicalRef = role.LogicalRef, Name = role.Name,
                                Reason = $"Field access for '{fp.FieldName}' on '{table.Name}' not applied: the field was not created." });
                            continue;
                        }

                        // PowerBase refuses to hide/lock a required field that has no default (users could not save). The source did it
                        // anyway; the restriction is kept (never widen access) and the consequence is reported.
                        if (fp.Access != "Modify" && field.IsRequired && string.IsNullOrWhiteSpace(field.DefaultValue))
                            skipped.Add(new ImportSkippedItem { LogicalRef = role.LogicalRef, Name = role.Name,
                                Reason = $"'{field.Label ?? field.Name}' on '{table.Name}' is required without a default and set to {fp.Access} for this role; the role cannot add records until that changes." });

                        rows.Add(new AppRoleFieldPermission { AppRoleId = created.Id, AppFieldId = field.Id, Access = fp.Access });
                    }

                    try
                    {
                        await _permRepo.SetFieldPermissionsAsync(created.Id, table.Id, rows, null, ct);
                    }
                    catch (DomainException ex)
                    {
                        failedTables.Add((table, $"field access could not be stored ({Describe(ex)})"));
                    }
                }
            }

            if (filterInputs.Count > 0)
            {
                try
                {
                    await _recordFiltersHandler.HandleAsync(new UpdateRecordFiltersCommand(created.PublicId, filterInputs), ct);
                }
                catch (DomainException ex)
                {
                    foreach (var f in filterInputs)
                        failedTables.Add((tableByName.Values.First(t => t.PublicId == f.TablePublicId), $"record filter could not be stored ({Describe(ex)})"));
                }
            }

            await RestrictInsteadAsync(role, created, failedTables, skipped, ct);
        }
    }

    /// <summary>A restriction that could not be stored leaves the role with no access to that table.</summary>
    private async Task RestrictInsteadAsync(PblRole role, AppRoleDetail created, List<(AppTable Table, string Reason)> failedTables, List<ImportSkippedItem> skipped, CancellationToken ct)
    {
        if (failedTables.Count == 0)
            return;

        var inputs = failedTables.Select(f => f.Table).DistinctBy(t => t.Id)
            .Select(t => new TablePermissionInput(t.PublicId, RecordScopes.None, RecordScopes.None, false, false, false, false, TableFieldAccessLevels.FullAccess))
            .ToList();

        try
        {
            await _tablePermissionsHandler.HandleAsync(new UpdateTablePermissionsCommand(created.PublicId, inputs), ct);
        }
        catch (DomainException)
        {
            // Nothing more can be done from here; the reasons below still tell the user to review the role.
        }

        foreach (var (table, reason) in failedTables)
            skipped.Add(new ImportSkippedItem { LogicalRef = role.LogicalRef, Name = role.Name,
                Reason = $"Table '{table.Name}': {reason}; the role was given no access to this table instead of unrestricted access." });
    }

    /// <summary>PBL filter tree → PowerBase's <see cref="FilterGroup"/> (conditions refer to fields by Fid).</summary>
    public static FilterGroup? ToFilterGroup(PblFilterGroup pbl, IReadOnlyList<AppField> fields, out string? problem)
    {
        problem = null;
        var byName = FieldIndex(fields);
        string? error = null;

        FilterGroup Convert(PblFilterGroup g)
        {
            var group = new FilterGroup { Logic = g.Logic };
            foreach (var c in g.Conditions)
            {
                if (!byName.TryGetValue(c.FieldName, out var field) || field.Fid is not { } fid)
                {
                    error ??= $"field '{c.FieldName}' does not exist";
                    continue;
                }

                var value = c.Value;
                // Quickbase writes a checkbox test as '1'/'0'; PowerBase stores booleans as true/false.
                if ((field.TypeCode is "Boolean" or "Formula_Bool") && value is "1" or "0")
                    value = value == "1" ? "true" : "false";

                var condition = new FilterCondition { FieldId = fid, Operator = c.Operator, Value = value };
                if (c.ValueMode is "ask" or "today" or "yesterday" or "tomorrow")
                {
                    condition.ValueMode = c.ValueMode;
                }
                else if (c.ValueMode == "field")
                {
                    if (c.ValueFieldName is null || !byName.TryGetValue(c.ValueFieldName, out var otherField) || otherField.Fid is not { } otherFid)
                    {
                        error ??= $"compared field '{c.ValueFieldName}' does not exist";
                        continue;
                    }
                    condition.ValueMode = "field";
                    condition.ValueFieldId = otherFid;
                }

                group.Nodes.Add(new FilterNode { Condition = condition });
            }
            foreach (var sub in g.Groups)
                group.Nodes.Add(new FilterNode { Group = Convert(sub) });
            return group;
        }

        var result = Convert(pbl);
        problem = error;
        return error is null && RoleRecordFilterJson.CountConditions(result) > 0 ? result : null;
    }

    private static Dictionary<string, AppField> FieldIndex(IReadOnlyList<AppField> fields)
    {
        var map = new Dictionary<string, AppField>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in fields) map.TryAdd(f.Name, f);
        foreach (var f in fields.Where(f => !string.IsNullOrWhiteSpace(f.Label))) map.TryAdd(f.Label!.Trim(), f);
        return map;
    }

    private static string Describe(Exception ex) =>
        ex is Domain.Exceptions.ValidationException ve ? string.Join(" ", ve.Errors.SelectMany(e => e.Value)) : ex.Message;
}
