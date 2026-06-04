using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.API.Models.Apps;
using PowerBase.Application.Apps.Commands.CreateAppRole;
using PowerBase.Application.Apps.Commands.DeleteAppRole;
using PowerBase.Application.Apps.Commands.UpdateAppRole;
using PowerBase.Application.Apps.Commands.UpdateTablePermissions;
using PowerBase.Application.Apps.Commands.UpdateFieldPermissions;
using PowerBase.Application.Apps.Commands.UpdateRecordFilters;
using PowerBase.Application.Apps.Queries.GetTablePermissions;
using PowerBase.Application.Apps.Queries.GetFieldPermissions;
using PowerBase.Application.Apps.Queries.GetRecordFilters;
using PowerBase.Application.Common.Models;

using PowerBase.Application.Apps.Queries.ListAppRoles;
using PowerBase.Domain.Constants;
using System.Text.Json;

namespace PowerBase.API.Controllers;

[ApiController]
[Route("apps/{appId:guid}/roles")]
[RequireAuth]
public class AppRolesController : ControllerBase
{
    private readonly ListAppRolesQueryHandler _listHandler;
    private readonly CreateAppRoleCommandHandler _createHandler;
    private readonly UpdateAppRoleCommandHandler _updateHandler;
    private readonly DeleteAppRoleCommandHandler _deleteHandler;
    private readonly GetTablePermissionsQueryHandler _getTablePermsHandler;
    private readonly UpdateTablePermissionsCommandHandler _updateTablePermsHandler;
    private readonly GetFieldPermissionsQueryHandler _getFieldPermsHandler;
    private readonly UpdateFieldPermissionsCommandHandler _updateFieldPermsHandler;
    private readonly GetRecordFiltersQueryHandler _getRecordFiltersHandler;
    private readonly UpdateRecordFiltersCommandHandler _updateRecordFiltersHandler;

    public AppRolesController(
        ListAppRolesQueryHandler listHandler,
        CreateAppRoleCommandHandler createHandler,
        UpdateAppRoleCommandHandler updateHandler,
        DeleteAppRoleCommandHandler deleteHandler,
        GetTablePermissionsQueryHandler getTablePermsHandler,
        UpdateTablePermissionsCommandHandler updateTablePermsHandler,
        GetFieldPermissionsQueryHandler getFieldPermsHandler,
        UpdateFieldPermissionsCommandHandler updateFieldPermsHandler,
        GetRecordFiltersQueryHandler getRecordFiltersHandler,
        UpdateRecordFiltersCommandHandler updateRecordFiltersHandler)
    {
        _listHandler = listHandler;
        _createHandler = createHandler;
        _updateHandler = updateHandler;
        _deleteHandler = deleteHandler;
        _getTablePermsHandler = getTablePermsHandler;
        _updateTablePermsHandler = updateTablePermsHandler;
        _getFieldPermsHandler = getFieldPermsHandler;
        _updateFieldPermsHandler = updateFieldPermsHandler;
        _getRecordFiltersHandler = getRecordFiltersHandler;
        _updateRecordFiltersHandler = updateRecordFiltersHandler;
    }

    /// <summary>List all roles defined for this app.</summary>
    [HttpGet]

    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<AppRoleResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromRoute] Guid appId, CancellationToken ct)
    {
        var roles = await _listHandler.HandleAsync(new ListAppRolesQuery(appId), ct);
        var response = roles.Select(r => new AppRoleResponse
        {
            PublicId = r.PublicId,
            Name = r.Name,
            IsDefault = r.IsDefault,
            IsSystem = r.IsSystem,
            Permissions = r.Permissions,
        }).ToList();
        return Ok(new ApiResponse<IReadOnlyList<AppRoleResponse>>(response));
    }

    /// <summary>Create a custom role for this app.</summary>
    [HttpPost]
    [RequireAppPermission(PermissionCodes.RolesManage, AppAccessResolver.ByAppId)]
    [ProducesResponseType(typeof(ApiResponse<AppRoleResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromRoute] Guid appId, [FromBody] CreateAppRoleRequest request, CancellationToken ct)
    {
        var result = await _createHandler.HandleAsync(new CreateAppRoleCommand(appId, request.Name, request.IsDefault), ct);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<AppRoleResponse>(new AppRoleResponse
        {
            PublicId = result.PublicId,
            Name = result.Name,
            IsDefault = result.IsDefault,
            IsSystem = false,
            Permissions = new[] { PermissionCodes.RecordsRead, PermissionCodes.RecordsCreate, PermissionCodes.RecordsUpdate },
        }));
    }

    /// <summary>Update the record permission flags for an app role.</summary>
    [HttpPatch("{rolePublicId:guid}")]
    [RequireAppPermission(PermissionCodes.RolesManage, AppAccessResolver.ByAppId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateRole(
        [FromRoute] Guid appId,
        [FromRoute] Guid rolePublicId,
        [FromBody] UpdateAppRoleRequest request,
        CancellationToken ct)
    {
        await _updateHandler.HandleAsync(new UpdateAppRoleCommand(appId, rolePublicId, request.Permissions), ct);
        return NoContent();
    }

    /// <summary>Delete a custom role. System roles (Administrator, Viewer) cannot be deleted.</summary>
    [HttpDelete("{rolePublicId:guid}")]
    [RequireAppPermission(PermissionCodes.RolesManage, AppAccessResolver.ByAppId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete([FromRoute] Guid appId, [FromRoute] Guid rolePublicId, CancellationToken ct)
    {
        await _deleteHandler.HandleAsync(new DeleteAppRoleCommand(appId, rolePublicId), ct);
        return NoContent();
    }

    // ── Table-level permissions ────────────────────────────────────────────────

    /// <summary>Get per-table permissions configured for a role.</summary>
    [HttpGet("{rolePublicId:guid}/table-permissions")]
    [RequireAppPermission(PermissionCodes.RolesManage, AppAccessResolver.ByAppId)]
    [ProducesResponseType(typeof(ApiResponse<TablePermissionsResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTablePermissions([FromRoute] Guid appId, [FromRoute] Guid rolePublicId, CancellationToken ct)
    {
        var rows = await _getTablePermsHandler.HandleAsync(new GetTablePermissionsQuery(rolePublicId), ct);
        var response = new TablePermissionsResponse
        {
            RolePublicId = rolePublicId,
            Tables = rows.Select(r => new TablePermissionDto
            {
                TablePublicId = r.TablePublicId,
                TableName = r.TableName,
                ViewScope = r.ViewScope,
                ModifyScope = r.ModifyScope,
                CanAdd = r.CanAdd,
                CanDelete = r.CanDelete,
                CanSaveSharedReports = r.CanSaveSharedReports,
                CanEditFieldProperties = r.CanEditFieldProperties,
                FieldAccessLevel = r.FieldAccessLevel,
            }).ToList(),
        };
        return Ok(new ApiResponse<TablePermissionsResponse>(response));
    }

    /// <summary>Replace the full set of per-table permissions for a role.</summary>
    [HttpPut("{rolePublicId:guid}/table-permissions")]
    [RequireAppPermission(PermissionCodes.RolesManage, AppAccessResolver.ByAppId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> UpdateTablePermissions(
        [FromRoute] Guid appId, [FromRoute] Guid rolePublicId,
        [FromBody] UpdateTablePermissionsRequest request, CancellationToken ct)
    {
        var inputs = (request.Tables ?? Array.Empty<TablePermissionDto>()).Select(t => new TablePermissionInput(
            t.TablePublicId, t.ViewScope, t.ModifyScope, t.CanAdd, t.CanDelete,
            t.CanSaveSharedReports, t.CanEditFieldProperties, t.FieldAccessLevel)).ToList();
        await _updateTablePermsHandler.HandleAsync(new UpdateTablePermissionsCommand(rolePublicId, inputs), ct);
        return NoContent();
    }

    // ── Field-level permissions ────────────────────────────────────────────────

    /// <summary>Get per-field permissions for a role on a specific table.</summary>
    [HttpGet("{rolePublicId:guid}/tables/{tableId:guid}/field-permissions")]
    [RequireAppPermission(PermissionCodes.RolesManage, AppAccessResolver.ByAppId)]
    [ProducesResponseType(typeof(ApiResponse<FieldPermissionsResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFieldPermissions(
        [FromRoute] Guid appId, [FromRoute] Guid rolePublicId, [FromRoute] Guid tableId, CancellationToken ct)
    {
        var rows = await _getFieldPermsHandler.HandleAsync(new GetFieldPermissionsQuery(rolePublicId, tableId), ct);
        var response = new FieldPermissionsResponse
        {
            RolePublicId = rolePublicId,
            TablePublicId = tableId,
            Fields = rows.Select(r => new FieldPermissionDto
            {
                FieldPublicId = r.FieldPublicId,
                FieldName = r.FieldName,
                Access = r.Access,
            }).ToList(),
        };
        return Ok(new ApiResponse<FieldPermissionsResponse>(response));
    }

    /// <summary>Replace per-field permissions for a role on a specific table.</summary>
    [HttpPut("{rolePublicId:guid}/tables/{tableId:guid}/field-permissions")]
    [RequireAppPermission(PermissionCodes.RolesManage, AppAccessResolver.ByAppId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> UpdateFieldPermissions(
        [FromRoute] Guid appId, [FromRoute] Guid rolePublicId, [FromRoute] Guid tableId,
        [FromBody] UpdateFieldPermissionsRequest request, CancellationToken ct)
    {
        var inputs = (request.Fields ?? Array.Empty<FieldPermissionDto>())
            .Select(f => new FieldPermissionInput(f.FieldPublicId, f.Access)).ToList();
        await _updateFieldPermsHandler.HandleAsync(new UpdateFieldPermissionsCommand(rolePublicId, tableId, inputs), ct);
        return NoContent();
    }

    // ── Record-level row filters ───────────────────────────────────────────────

    /// <summary>Get per-table record filters for a role.</summary>
    [HttpGet("{rolePublicId:guid}/record-filters")]
    [RequireAppPermission(PermissionCodes.RolesManage, AppAccessResolver.ByAppId)]
    [ProducesResponseType(typeof(ApiResponse<RecordLevelFiltersResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRecordFilters([FromRoute] Guid appId, [FromRoute] Guid rolePublicId, CancellationToken ct)
    {
        var rows = await _getRecordFiltersHandler.HandleAsync(new GetRecordFiltersQuery(rolePublicId), ct);
        var response = new RecordLevelFiltersResponse
        {
            RolePublicId = rolePublicId,
            Filters = rows.Select(r =>
            {
                var conditions = string.IsNullOrWhiteSpace(r.FilterJson)
                    ? new List<RoleRecordFilterCondition>()
                    : JsonSerializer.Deserialize<List<RoleRecordFilterCondition>>(r.FilterJson) ?? new();
                return new RecordLevelFilterDto
                {
                    TablePublicId = r.TablePublicId,
                    Conjunction = r.Conjunction,
                    Conditions = conditions.Select(c => new RecordFilterConditionDto
                    {
                        FieldPublicId = c.FieldPublicId,
                        Operator = c.Operator,
                        Value = c.Value,
                        UseCurrentUser = c.UseCurrentUser,
                    }).ToList(),
                };
            }).ToList(),
        };
        return Ok(new ApiResponse<RecordLevelFiltersResponse>(response));
    }

    /// <summary>Replace per-table record filters for a role.</summary>
    [HttpPut("{rolePublicId:guid}/record-filters")]
    [RequireAppPermission(PermissionCodes.RolesManage, AppAccessResolver.ByAppId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> UpdateRecordFilters(
        [FromRoute] Guid appId, [FromRoute] Guid rolePublicId,
        [FromBody] UpdateRecordLevelFiltersRequest request, CancellationToken ct)
    {
        var inputs = (request.Filters ?? Array.Empty<RecordLevelFilterDto>()).Select(f => new RecordFilterInput(
            f.TablePublicId,
            f.Conjunction,
            (f.Conditions ?? Array.Empty<RecordFilterConditionDto>())
                .Select(c => new RoleRecordFilterCondition(c.FieldPublicId, c.Operator, c.Value, c.UseCurrentUser)).ToList()
        )).ToList();
        await _updateRecordFiltersHandler.HandleAsync(new UpdateRecordFiltersCommand(rolePublicId, inputs), ct);
        return NoContent();
    }
}
