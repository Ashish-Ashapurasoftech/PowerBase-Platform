using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.API.Models.Apps;
using PowerBase.Application.Apps.Commands.AddAppUser;
using PowerBase.Application.Apps.Commands.ChangeAppUserRole;
using PowerBase.Application.Apps.Commands.InviteAppUser;
using PowerBase.Application.Apps.Commands.RemoveAppUser;
using PowerBase.Application.Apps.Commands.UpdateUserPickerVisibility;
using PowerBase.Application.Apps.Queries.ListAppUsers;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.API.Controllers;

[ApiController]
[Route("apps/{appId:guid}/users")]
[RequireAuth]
public class AppUsersController : ControllerBase
{
    private readonly ListAppUsersQueryHandler _listHandler;
    private readonly ListAppUsersForPickerQueryHandler _listPickerHandler;
    private readonly AddAppUserCommandHandler _addHandler;
    private readonly InviteAppUserCommandHandler _inviteHandler;
    private readonly ChangeAppUserRoleCommandHandler _changeRoleHandler;
    private readonly UpdateUserPickerVisibilityCommandHandler _updatePickerVisibilityHandler;
    private readonly RemoveAppUserCommandHandler _removeHandler;
    private readonly string _frontendBaseUrl;

    public AppUsersController(
        ListAppUsersQueryHandler listHandler,
        ListAppUsersForPickerQueryHandler listPickerHandler,
        AddAppUserCommandHandler addHandler,
        InviteAppUserCommandHandler inviteHandler,
        ChangeAppUserRoleCommandHandler changeRoleHandler,
        UpdateUserPickerVisibilityCommandHandler updatePickerVisibilityHandler,
        RemoveAppUserCommandHandler removeHandler,
        IConfiguration config)
    {
        _listHandler = listHandler;
        _listPickerHandler = listPickerHandler;
        _addHandler = addHandler;
        _inviteHandler = inviteHandler;
        _changeRoleHandler = changeRoleHandler;
        _updatePickerVisibilityHandler = updatePickerVisibilityHandler;
        _removeHandler = removeHandler;
        _frontendBaseUrl = config["Frontend:BaseUrl"] ?? "http://localhost:4200";
    }

    /// <summary>List all users with access to this app (paginated).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiListResponse<AppUserResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        Guid appId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null,
        [FromQuery] string sortBy = "userName",
        [FromQuery] bool sortDesc = false,
        [FromQuery] string? sortOrder = null,
        [FromQuery] string? role = null,
        CancellationToken ct = default)
    {
        bool actualSortDesc = sortDesc || sortOrder?.ToLower() == "desc";

        var result = await _listHandler.HandleAsync(new ListAppUsersQuery(
            AppPublicId: appId,
            Page: page,
            PageSize: pageSize,
            Search: search,
            SortBy: sortBy,
            SortDesc: actualSortDesc,
            Role: role), ct);

        var items = result.Items.Select(MapToListItemResponse).ToList();
        return Ok(new ApiListResponse<AppUserResponse>(items, result.Total, result.Page, result.PageSize));
    }

    private static AppUserResponse MapToListItemResponse(AppUserResult u)
    {
        return new AppUserResponse
        {
            PublicId = u.PublicId,
            UserPublicId = u.UserPublicId,
            UserName = u.UserName,
            UserEmail = u.UserEmail,
            RolePublicId = u.RolePublicId,
            RoleName = u.RoleName,
            Status = u.Status,
            ShowInUserPickers = u.ShowInUserPickers,
            AddedOn = u.AddedOn.ToString("o"),
            IsOwner = u.IsOwner,
            IsFromGroup = u.IsFromGroup,
        };
    }

    /// <summary>List users configured to be visible in user pickers for this app.</summary>
    [HttpGet("picker")]
    [ProducesResponseType(typeof(IReadOnlyList<AppUserPickerResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListForPicker([FromRoute] Guid appId, CancellationToken ct)
    {
        var users = await _listPickerHandler.HandleAsync(new ListAppUsersForPickerQuery(appId), ct);
        var response = users.Select(u => new AppUserPickerResponse(u.UserPublicId, u.UserName, u.UserEmail)).ToList();
        return Ok(response);
    }

    /// <summary>Export all users of this app to CSV.</summary>
    [HttpGet("export")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> Export(
        [FromRoute] Guid appId,
        [FromQuery] string? search = null,
        [FromQuery] string sortBy = "userName",
        [FromQuery] bool sortDesc = false,
        [FromQuery] string? sortOrder = null,
        [FromQuery] string? role = null,
        CancellationToken ct = default)
    {
        bool actualSortDesc = sortDesc || sortOrder?.ToLower() == "desc";

        var result = await _listHandler.HandleAsync(new ListAppUsersQuery(
            AppPublicId: appId,
            Page: 1,
            PageSize: 1,
            Search: search,
            SortBy: sortBy,
            SortDesc: actualSortDesc,
            Role: role,
            IsExport: true), ct);

        var csvBuilder = new System.Text.StringBuilder();
        csvBuilder.AppendLine("Name,Email,AccessVia,AppRole,AddedOn");

        foreach (var user in result.Items)
        {
            csvBuilder.AppendLine(string.Join(",",
                EscapeCsvField(user.UserName),
                EscapeCsvField(user.UserEmail),
                EscapeCsvField(user.IsFromGroup ? "Group" : "Individual"),
                EscapeCsvField(user.RoleName),
                EscapeCsvField(user.AddedOn.ToString("o"))
            ));
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(csvBuilder.ToString());
        return File(bytes, "text/csv", "users.csv");
    }

    private static string EscapeCsvField(string? field)
    {
        if (string.IsNullOrEmpty(field))
        {
            return string.Empty;
        }

        bool mustQuote = field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r');
        if (mustQuote)
        {
            return $"\"{field.Replace("\"", "\"\"")}\"";
        }

        return field;
    }


    /// <summary>Add a user to this app by email (must already be a tenant member).</summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Add([FromRoute] Guid appId, [FromBody] AddAppUserRequest request, CancellationToken ct)
    {
        await _addHandler.HandleAsync(new AddAppUserCommand(appId, request.Email, request.RolePublicId), ct);
        return NoContent();
    }

    /// <summary>
    /// Invite a user to this app by email.
    /// If the user already has an active PowerBase account they are added immediately
    /// and receive an informational email. Otherwise a setup email is sent.
    /// </summary>
    [HttpPost("invite")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Invite([FromRoute] Guid appId, [FromBody] InviteAppUserRequest request, CancellationToken ct)
    {
        await _inviteHandler.HandleAsync(
            new InviteAppUserCommand(appId, request.Email, request.RolePublicId, _frontendBaseUrl), ct);
        return NoContent();
    }

    /// <summary>Change the role of an existing app user.</summary>
    [HttpPatch("{userPublicId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ChangeRole(
        [FromRoute] Guid appId,
        [FromRoute] Guid userPublicId,
        [FromBody] ChangeAppUserRoleRequest request,
        CancellationToken ct)
    {
        await _changeRoleHandler.HandleAsync(new ChangeAppUserRoleCommand(appId, userPublicId, request.RolePublicId), ct);
        return NoContent();
    }

    /// <summary>Toggle 'Show in User Pickers' for an app user.</summary>
    [HttpPatch("{userPublicId:guid}/user-picker-visibility")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateUserPickerVisibility(
        [FromRoute] Guid appId,
        [FromRoute] Guid userPublicId,
        [FromBody] UpdateUserPickerVisibilityRequest request,
        CancellationToken ct)
    {
        await _updatePickerVisibilityHandler.HandleAsync(
            new UpdateUserPickerVisibilityCommand(appId, userPublicId, request.ShowInUserPickers), ct);
        return NoContent();
    }

    /// <summary>Remove a user from this app.</summary>
    [HttpDelete("{userPublicId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Remove([FromRoute] Guid appId, [FromRoute] Guid userPublicId, CancellationToken ct)
    {
        await _removeHandler.HandleAsync(new RemoveAppUserCommand(appId, userPublicId), ct);
        return NoContent();
    }
}

public record InviteAppUserRequest(string Email, Guid? RolePublicId);
public record UpdateUserPickerVisibilityRequest(bool ShowInUserPickers);
public record AppUserPickerResponse(Guid UserPublicId, string UserName, string UserEmail);
