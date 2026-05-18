using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.API.Models.Roles;
using PowerBase.Application.Roles;
using PowerBase.Application.Roles.Commands.CreateRole;
using PowerBase.Application.Roles.Commands.UpdateRolePermissions;
using PowerBase.Application.Roles.Queries.GetRolePermissions;
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
    private readonly GetRolePermissionsQueryHandler _getPermsHandler;
    private readonly UpdateRolePermissionsCommandHandler _updatePermsHandler;

    public RolesController(
        ListRolesQueryHandler listHandler,
        CreateRoleCommandHandler createHandler,
        GetRolePermissionsQueryHandler getPermsHandler,
        UpdateRolePermissionsCommandHandler updatePermsHandler)
    {
        _listHandler = listHandler;
        _createHandler = createHandler;
        _getPermsHandler = getPermsHandler;
        _updatePermsHandler = updatePermsHandler;
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
