using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.API.Models.Apps;
using PowerBase.Application.Apps.Commands.CreateAppRole;
using PowerBase.Application.Apps.Commands.DeleteAppRole;
using PowerBase.Application.Apps.Queries.ListAppRoles;

namespace PowerBase.API.Controllers;

[ApiController]
[Route("apps/{appId:guid}/roles")]
[RequireAuth]
public class AppRolesController : ControllerBase
{
    private readonly ListAppRolesQueryHandler _listHandler;
    private readonly CreateAppRoleCommandHandler _createHandler;
    private readonly DeleteAppRoleCommandHandler _deleteHandler;

    public AppRolesController(
        ListAppRolesQueryHandler listHandler,
        CreateAppRoleCommandHandler createHandler,
        DeleteAppRoleCommandHandler deleteHandler)
    {
        _listHandler = listHandler;
        _createHandler = createHandler;
        _deleteHandler = deleteHandler;
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
        }).ToList();
        return Ok(new ApiResponse<IReadOnlyList<AppRoleResponse>>(response));
    }

    /// <summary>Create a custom role for this app.</summary>
    [HttpPost]
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
        }));
    }

    /// <summary>Delete a custom role. System roles (Administrator, Viewer) cannot be deleted.</summary>
    [HttpDelete("{rolePublicId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete([FromRoute] Guid appId, [FromRoute] Guid rolePublicId, CancellationToken ct)
    {
        await _deleteHandler.HandleAsync(new DeleteAppRoleCommand(appId, rolePublicId), ct);
        return NoContent();
    }
}
