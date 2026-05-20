using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.API.Models.Roles;
using PowerBase.Application.Roles;
using PowerBase.Application.Roles.Commands.CreateRole;
using PowerBase.Application.Roles.Commands.DeleteRole;
using PowerBase.Application.Roles.Commands.UpdateRole;
using PowerBase.Application.Roles.Commands.UpdateRolePermissions;
using PowerBase.Application.Roles.Queries.GetRolePermissions;
using PowerBase.Application.Roles.Queries.ListPermissions;
using PowerBase.Application.Roles.Queries.ListRoles;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.API.Controllers;

[ApiController]
[Route("roles")]
public class RolesController : ControllerBase
{
    private readonly ListRolesQueryHandler _listHandler;
    private readonly CreateRoleCommandHandler _createHandler;
    private readonly UpdateRoleCommandHandler _updateHandler;
    private readonly DeleteRoleCommandHandler _deleteHandler;
    private readonly GetRolePermissionsQueryHandler _getPermsHandler;
    private readonly UpdateRolePermissionsCommandHandler _updatePermsHandler;
    private readonly ListPermissionsQueryHandler _listAllPermsHandler;

    public RolesController(
        ListRolesQueryHandler listHandler,
        CreateRoleCommandHandler createHandler,
        UpdateRoleCommandHandler updateHandler,
        DeleteRoleCommandHandler deleteHandler,
        GetRolePermissionsQueryHandler getPermsHandler,
        UpdateRolePermissionsCommandHandler updatePermsHandler,
        ListPermissionsQueryHandler listAllPermsHandler)
    {
        _listHandler = listHandler;
        _createHandler = createHandler;
        _updateHandler = updateHandler;
        _deleteHandler = deleteHandler;
        _getPermsHandler = getPermsHandler;
        _updatePermsHandler = updatePermsHandler;
        _listAllPermsHandler = listAllPermsHandler;
    }

    /// <summary>List all roles for the current tenant.</summary>
    [HttpGet]
    [RequirePermission(PermissionCodes.RolesManage)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<RoleResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var roles = await _listHandler.HandleAsync(new ListRolesQuery(), ct);
        var items = roles.Select(MapToResponse).ToList();
        return Ok(new ApiResponse<IReadOnlyList<RoleResponse>>(items));
    }

    /// <summary>Create a new role for the current tenant.</summary>
    [HttpPost]
    [RequirePermission(PermissionCodes.RolesManage)]
    [ProducesResponseType(typeof(ApiResponse<RoleResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateRoleRequest request, CancellationToken ct)
    {
        var command = new CreateRoleCommand(request.Name, request.Description, request.PermissionCodes);
        var result = await _createHandler.HandleAsync(command, ct);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<RoleResponse>(MapToResponse(result)));
    }

    /// <summary>Update a custom role's name and description.</summary>
    [HttpPatch("{publicId:guid}")]
    [RequirePermission(PermissionCodes.RolesManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid publicId, [FromBody] UpdateRoleRequest request, CancellationToken ct)
    {
        await _updateHandler.HandleAsync(new UpdateRoleCommand(publicId, request.Name, request.Description), ct);
        return NoContent();
    }

    /// <summary>Delete a custom role. System roles cannot be deleted.</summary>
    [HttpDelete("{publicId:guid}")]
    [RequirePermission(PermissionCodes.RolesManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid publicId, CancellationToken ct)
    {
        await _deleteHandler.HandleAsync(new DeleteRoleCommand(publicId), ct);
        return NoContent();
    }

    /// <summary>Get the permissions assigned to a role.</summary>
    [HttpGet("{publicId:guid}/permissions")]
    [RequirePermission(PermissionCodes.RolesManage)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<PermissionResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPermissions(Guid publicId, CancellationToken ct)
    {
        var perms = await _getPermsHandler.HandleAsync(new GetRolePermissionsQuery(publicId), ct);
        var items = perms.Select(MapPermissionToResponse).ToList();
        return Ok(new ApiResponse<IReadOnlyList<PermissionResponse>>(items));
    }

    /// <summary>List all permissions available in the system.</summary>
    [HttpGet("/permissions")]
    [RequireAuth]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<PermissionResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListAllPermissions(CancellationToken ct)
    {
        var perms = await _listAllPermsHandler.HandleAsync(new ListPermissionsQuery(), ct);
        return Ok(new ApiResponse<IReadOnlyList<PermissionResponse>>(perms.Select(MapPermissionToResponse).ToList()));
    }

    /// <summary>Replace the permissions assigned to a role.</summary>
    [HttpPut("{publicId:guid}/permissions")]
    [RequirePermission(PermissionCodes.RolesManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdatePermissions(Guid publicId, [FromBody] UpdateRolePermissionsRequest request, CancellationToken ct)
    {
        await _updatePermsHandler.HandleAsync(new UpdateRolePermissionsCommand(publicId, request.PermissionCodes), ct);
        return NoContent();
    }

    private static RoleResponse MapToResponse(RoleResult r) => new()
    {
        PublicId = r.PublicId,
        Name = r.Name,
        Description = r.Description,
        IsDefault = r.IsDefault,
        IsSystem = r.IsSystem,
        CreatedOn = r.CreatedOn,
    };

    private static PermissionResponse MapPermissionToResponse(Permission p) => new()
    {
        Code = p.Code,
        DisplayName = p.DisplayName,
        Description = p.Description,
    };
}
