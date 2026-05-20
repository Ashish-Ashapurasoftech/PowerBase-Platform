using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.Application.Common.Interfaces;
using PowerBase.API.Models;
using PowerBase.API.Models.Apps;
using PowerBase.Application.Apps.Commands.CreateApp;
using PowerBase.Application.Apps.Commands.DeleteApp;
using PowerBase.Application.Apps.Commands.UpdateApp;
using PowerBase.Application.Apps.Queries.GetApp;
using PowerBase.Application.Apps.Queries.ListApps;
using PowerBase.Domain.Constants;

namespace PowerBase.API.Controllers;

[ApiController]
[Route("apps")]
public class AppsController : ControllerBase
{
    private readonly CreateAppCommandHandler _createHandler;
    private readonly UpdateAppCommandHandler _updateHandler;
    private readonly DeleteAppCommandHandler _deleteHandler;
    private readonly GetAppQueryHandler _getHandler;
    private readonly ListAppsQueryHandler _listHandler;

    public AppsController(
        CreateAppCommandHandler createHandler,
        UpdateAppCommandHandler updateHandler,
        DeleteAppCommandHandler deleteHandler,
        GetAppQueryHandler getHandler,
        ListAppsQueryHandler listHandler)
    {
        _createHandler = createHandler;
        _updateHandler = updateHandler;
        _deleteHandler = deleteHandler;
        _getHandler = getHandler;
        _listHandler = listHandler;
    }

    /// <summary>Create a new app for the current tenant.</summary>
    [HttpPost]
    [RequirePermission(PermissionCodes.AppsCreate)]
    [ProducesResponseType(typeof(ApiResponse<AppResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateAppRequest request, CancellationToken ct)
    {
        var command = new CreateAppCommand(request.Name, request.Description, request.Icon, request.Color);
        var result = await _createHandler.HandleAsync(command, ct);
        var response = MapToAppResponse(result);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<AppResponse>(response));
    }

    /// <summary>List all apps for the current tenant.</summary>
    [HttpGet]
    [RequirePermission(PermissionCodes.AppsRead)]
    [ProducesResponseType(typeof(ApiListResponse<AppResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var result = await _listHandler.HandleAsync(new ListAppsQuery(page, pageSize), ct);
        var items = result.Items.Select(MapToAppResponse).ToList();
        return Ok(new ApiListResponse<AppResponse>(items, result.Total, result.Page, result.PageSize));
    }

    /// <summary>Get a single app by its public ID.</summary>
    [HttpGet("{publicId:guid}")]
    [RequirePermission(PermissionCodes.AppsRead)]
    [RequireAppAccess(AppAccess.Read, AppAccessResolver.ByAppPublicId)]
    [ProducesResponseType(typeof(ApiResponse<AppResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid publicId, CancellationToken ct)
    {
        var app = await _getHandler.HandleAsync(new GetAppQuery(publicId), ct);
        return Ok(new ApiResponse<AppResponse>(MapToAppResponse(app)));
    }

    /// <summary>Update an app's name, description, icon, or color.</summary>
    [HttpPatch("{publicId:guid}")]
    [RequirePermission(PermissionCodes.AppsUpdate)]
    [RequireAppAccess(AppAccess.Admin, AppAccessResolver.ByAppPublicId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid publicId, [FromBody] UpdateAppRequest request, CancellationToken ct)
    {
        await _updateHandler.HandleAsync(new UpdateAppCommand(publicId, request.Name, request.Description, request.Icon, request.Color), ct);
        return NoContent();
    }

    /// <summary>Soft-delete an app by its public ID.</summary>
    [HttpDelete("{publicId:guid}")]
    [RequirePermission(PermissionCodes.AppsDelete)]
    [RequireAppAccess(AppAccess.Admin, AppAccessResolver.ByAppPublicId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid publicId, CancellationToken ct)
    {
        await _deleteHandler.HandleAsync(new DeleteAppCommand(publicId), ct);
        return NoContent();
    }

    private static AppResponse MapToAppResponse(CreateAppResult result) => new()
    {
        PublicId = result.PublicId,
        Name = result.Name,
        Description = result.Description,
        Icon = result.Icon,
        Color = result.Color,
        Status = result.Status,
        CreatedOn = result.CreatedOn,
    };

    private static AppResponse MapToAppResponse(PowerBase.Domain.Entities.App app) => new()
    {
        PublicId = app.PublicId,
        Name = app.Name,
        Description = app.Description,
        Icon = app.Icon,
        Color = app.Color,
        Status = app.Status,
        CreatedOn = app.CreatedOn,
    };
}
