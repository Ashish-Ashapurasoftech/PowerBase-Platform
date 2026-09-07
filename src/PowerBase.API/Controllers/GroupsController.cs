using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.API.Models.Groups;
using PowerBase.Domain.Constants;
using System.ComponentModel.DataAnnotations;
using PowerBase.Application.Groups.Common;
using PowerBase.Application.Groups.Commands.AddGroupMembers;
using PowerBase.Application.Groups.Commands.CreateGroup;
using PowerBase.Application.Groups.Commands.DeleteGroup;
using PowerBase.Application.Groups.Commands.RemoveGroupMember;
using PowerBase.Application.Groups.Commands.UpdateGroup;
using PowerBase.Application.Groups.Commands.ShareGroupWithApp;
using PowerBase.Application.Groups.Commands.UnshareGroupFromApp;
using PowerBase.Application.Groups.Queries.GetGroup;
using PowerBase.Application.Groups.Queries.ListGroupMembers;
using PowerBase.Application.Groups.Queries.ListGroups;
using PowerBase.Application.Groups.Queries.GetUserEffectivePermissions;
using PowerBase.Application.Groups.Queries.GetSharedApps;
using PowerBase.Application.Groups.Queries.GetMyGroups;
using PowerBase.Domain.Exceptions;

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
    private readonly GetMyGroupsQueryHandler _getMyGroupsHandler;
    private readonly GetGroupQueryHandler _getHandler;
    private readonly AddGroupMembersCommandHandler _addMembersHandler;
    private readonly RemoveGroupMemberCommandHandler _removeMemberHandler;
    private readonly ListGroupMembersQueryHandler _listMembersHandler;
    private readonly GetUserEffectivePermissionsQueryHandler _effectivePermissionsHandler;
    private readonly ShareGroupWithAppCommandHandler _shareHandler;
    private readonly UnshareGroupFromAppCommandHandler _unshareHandler;
    private readonly GetSharedAppsQueryHandler _getSharedAppsHandler;

    public GroupsController(
        CreateGroupCommandHandler createHandler,
        UpdateGroupCommandHandler updateHandler,
        DeleteGroupCommandHandler deleteHandler,
        ListGroupsQueryHandler listHandler,
        GetMyGroupsQueryHandler getMyGroupsHandler,
        GetGroupQueryHandler getHandler,
        AddGroupMembersCommandHandler addMembersHandler,
        RemoveGroupMemberCommandHandler removeMemberHandler,
        ListGroupMembersQueryHandler listMembersHandler,
        GetUserEffectivePermissionsQueryHandler effectivePermissionsHandler,
        ShareGroupWithAppCommandHandler shareHandler,
        UnshareGroupFromAppCommandHandler unshareHandler,
        GetSharedAppsQueryHandler getSharedAppsHandler)
    {
        _createHandler = createHandler;
        _updateHandler = updateHandler;
        _deleteHandler = deleteHandler;
        _listHandler = listHandler;
        _getMyGroupsHandler = getMyGroupsHandler;
        _getHandler = getHandler;
        _addMembersHandler = addMembersHandler;
        _removeMemberHandler = removeMemberHandler;
        _listMembersHandler = listMembersHandler;
        _effectivePermissionsHandler = effectivePermissionsHandler;
        _shareHandler = shareHandler;
        _unshareHandler = unshareHandler;
        _getSharedAppsHandler = getSharedAppsHandler;
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
    [ProducesResponseType(typeof(ApiListResponse<GroupDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListGroups(
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var query = new ListGroupsQuery 
        { 
            Search = search, 
            Page = page, 
            PageSize = pageSize 
        };
        var result = await _listHandler.HandleAsync(query, ct);
        return Ok(new ApiListResponse<GroupDto>(result.Items, result.Total, result.Page, result.PageSize));
    }

    /// <summary>Get groups the current logged-in user is a member of</summary>
    [HttpGet("my")]
    public async Task<IActionResult> GetMyGroups(CancellationToken ct = default)
    {
        var result = await _getMyGroupsHandler.HandleAsync(new GetMyGroupsQuery(), ct);
        return Ok(new { data = result });
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

    /// <summary>Update group name and description</summary>
    [HttpPut("{publicId:guid}")]
    [RequirePermission(PermissionCodes.AppsCreate)]
    public async Task<IActionResult> UpdateGroup(
        [FromRoute] Guid publicId, 
        [FromBody] UpdateGroupRequest request, 
        CancellationToken ct)
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

    // ── MEMBERS ──────────────────────────────────────────────────────────────

    /// <summary>Add one or more tenant users to a group</summary>
    [HttpPost("{publicId:guid}/members")]
    [RequirePermission(PermissionCodes.AppsCreate)]
    public async Task<IActionResult> AddMembers(
        [FromRoute] Guid publicId, 
        [FromBody] AddGroupMembersRequest request, 
        CancellationToken ct)
    {
        var command = new AddGroupMembersCommand 
        { 
            GroupPublicId = publicId, 
            UserPublicIds = request.UserPublicIds 
        };
        var added = await _addMembersHandler.HandleAsync(command, ct);
        return Ok(new { data = added });
    }

    /// <summary>Remove a single tenant user from a group</summary>
    [HttpDelete("{publicId:guid}/members/{userPublicId:guid}")]
    [RequirePermission(PermissionCodes.AppsCreate)]
    public async Task<IActionResult> RemoveMember(
        [FromRoute] Guid publicId, 
        [FromRoute] Guid userPublicId, 
        CancellationToken ct)
    {
        var command = new RemoveGroupMemberCommand { GroupPublicId = publicId, UserPublicId = userPublicId };
        await _removeMemberHandler.HandleAsync(command, ct);
        return Ok(new { data = true });
    }

    /// <summary>List members of a group (paginated)</summary>
    [HttpGet("{publicId:guid}/members")]
    [RequirePermission(PermissionCodes.AppsRead)]
    [ProducesResponseType(typeof(ApiListResponse<GroupMemberDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListMembers(
        [FromRoute] Guid publicId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var query = new ListGroupMembersQuery { GroupPublicId = publicId, Page = page, PageSize = pageSize };
        var result = await _listMembersHandler.HandleAsync(query, ct);
        return Ok(new ApiListResponse<GroupMemberDto>(result.Items, result.Total, result.Page, result.PageSize));
    }

    /// <summary>Get consolidated permissions and app access for a user</summary>
    [HttpGet("users/{userPublicId:guid}/effective-permissions")]
    [RequirePermission(PermissionCodes.AppsRead)]
    public async Task<IActionResult> GetUserEffectivePermissions([FromRoute] Guid userPublicId, CancellationToken ct)
    {
        var query = new GetUserEffectivePermissionsQuery { UserPublicId = userPublicId };
        var result = await _effectivePermissionsHandler.HandleAsync(query, ct);
        return Ok(new { data = result });
    }

    // ── APP SHARING ──────────────────────────────────────────────────────────

    /// <summary>Share a group with apps using default roles.</summary>
    [HttpPost("{publicId:guid}/apps")]
    [RequirePermission(PermissionCodes.AppsCreate)]
    public async Task<IActionResult> ShareGroup(
        [FromRoute] Guid publicId, 
        [FromBody] ShareGroupRequest request, 
        CancellationToken ct)
    {
        var command = new ShareGroupWithAppCommand
        {
            GroupPublicId = publicId,
            AppPublicIds = request.AppPublicIds,
            AppRolePublicId = request.AppRolePublicId
        };
        var success = await _shareHandler.HandleAsync(command, ct);
        if (!success)
            throw new NotFoundException("GroupShare", $"Group: {publicId}, Apps Count: {request.AppPublicIds.Count()}");

        return NoContent();
    }

    /// <summary>Unshare a group from an app.</summary>
    [HttpDelete("{publicId:guid}/apps/{appPublicId:guid}")]
    [RequirePermission(PermissionCodes.AppsCreate)]
    public async Task<IActionResult> UnshareGroup(
        [FromRoute] Guid publicId, 
        [FromRoute] Guid appPublicId, 
        CancellationToken ct)
    {
        var command = new UnshareGroupFromAppCommand
        {
            GroupPublicId = publicId,
            AppPublicId = appPublicId
        };
        var success = await _unshareHandler.HandleAsync(command, ct);
        if (!success)
            throw new NotFoundException("GroupShare", $"Group: {publicId}, App: {appPublicId}");

        return NoContent();
    }

    /// <summary>Get shared apps of a group.</summary>
    [HttpGet("{publicId:guid}/apps")]
    [RequirePermission(PermissionCodes.AppsRead)]
    public async Task<IActionResult> GetSharedApps([FromRoute] Guid publicId, CancellationToken ct)
    {
        var query = new GetSharedAppsQuery { GroupPublicId = publicId };
        var sharedApps = await _getSharedAppsHandler.HandleAsync(query, ct);
        return Ok(new { data = sharedApps });
    }
}
