using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models.Groups;
using PowerBase.Domain.Constants;
using PowerBase.Application.Groups.Commands.CreateGroup;
using PowerBase.Application.Groups.Commands.DeleteGroup;
using PowerBase.Application.Groups.Commands.UpdateGroup;
using PowerBase.Application.Groups.Queries.GetGroup;
using PowerBase.Application.Groups.Queries.ListGroups;

namespace PowerBase.API.Controllers;

[ApiController]
[Route("groups")]
[RequireAuth]
public class GroupsController : ControllerBase
{
    private readonly CreateGroupCommandHandler _createHandler;
    private readonly UpdateGroupCommandHandler _updateHandler;
    private readonly DeleteGroupCommandHandler _deleteHandler;
    private readonly ListGroupsQueryHandler _listHandler;
    private readonly GetGroupQueryHandler _getHandler;

    public GroupsController(
        CreateGroupCommandHandler createHandler,
        UpdateGroupCommandHandler updateHandler,
        DeleteGroupCommandHandler deleteHandler,
        ListGroupsQueryHandler listHandler,
        GetGroupQueryHandler getHandler)
    {
        _createHandler = createHandler;
        _updateHandler = updateHandler;
        _deleteHandler = deleteHandler;
        _listHandler = listHandler;
        _getHandler = getHandler;
    }

    /// <summary>Create a new group</summary>
    [HttpPost]
    [RequirePermission(PermissionCodes.AppsCreate)]
    public async Task<IActionResult> CreateGroup([FromBody] CreateGroupRequest request, CancellationToken ct)
    {
        var command = new CreateGroupCommand 
        { 
            Name = request.Name, 
            Description = request.Description
        };
        var result = await _createHandler.HandleAsync(command, ct);
        return Ok(new { data = result });
    }

    /// <summary>List all groups (paginated)</summary>
    [HttpGet]
    [RequirePermission(PermissionCodes.AppsRead)]
    public async Task<IActionResult> ListGroups(
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var query = new ListGroupsQuery { Search = search, Page = page, PageSize = pageSize };
        var (items, total) = await _listHandler.HandleAsync(query, ct);
        return Ok(new { data = items, total });
    }

    /// <summary>Get a single group</summary>
    [HttpGet("{publicId:guid}")]
    [RequirePermission(PermissionCodes.AppsRead)]
    public async Task<IActionResult> GetGroup([FromRoute] Guid publicId, CancellationToken ct)
    {
        var query = new GetGroupQuery { PublicId = publicId };
        var result = await _getHandler.HandleAsync(query, ct);
        return Ok(new { data = result });
    }

    /// <summary>Update group name/description</summary>
    [HttpPut("{publicId:guid}")]
    [RequirePermission(PermissionCodes.AppsCreate)]
    public async Task<IActionResult> UpdateGroup([FromRoute] Guid publicId, [FromBody] UpdateGroupRequest request, CancellationToken ct)
    {
        var command = new UpdateGroupCommand 
        { 
            PublicId = publicId, 
            Name = request.Name, 
            Description = request.Description
        };
        await _updateHandler.HandleAsync(command, ct);
        return Ok(new { data = true });
    }

    /// <summary>Delete a group</summary>
    [HttpDelete("{publicId:guid}")]
    [RequirePermission(PermissionCodes.AppsCreate)]
    public async Task<IActionResult> DeleteGroup([FromRoute] Guid publicId, CancellationToken ct)
    {
        var command = new DeleteGroupCommand { PublicId = publicId };
        await _deleteHandler.HandleAsync(command, ct);
        return Ok(new { data = true });
    }
}
