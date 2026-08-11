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

namespace PowerBase.API.Controllers;

[ApiController]
[Route("apps/{appId:guid}/users")]
[RequireAuth]
public class AppUsersController : ControllerBase
{
    private readonly ListAppUsersQueryHandler _listHandler;
    private readonly AddAppUserCommandHandler _addHandler;
    private readonly InviteAppUserCommandHandler _inviteHandler;
    private readonly ChangeAppUserRoleCommandHandler _changeRoleHandler;
    private readonly UpdateUserPickerVisibilityCommandHandler _updatePickerVisibilityHandler;
    private readonly RemoveAppUserCommandHandler _removeHandler;
    private readonly string _frontendBaseUrl;

    public AppUsersController(
        ListAppUsersQueryHandler listHandler,
        AddAppUserCommandHandler addHandler,
        InviteAppUserCommandHandler inviteHandler,
        ChangeAppUserRoleCommandHandler changeRoleHandler,
        UpdateUserPickerVisibilityCommandHandler updatePickerVisibilityHandler,
        RemoveAppUserCommandHandler removeHandler,
        IConfiguration config)
    {
        _listHandler = listHandler;
        _addHandler = addHandler;
        _inviteHandler = inviteHandler;
        _changeRoleHandler = changeRoleHandler;
        _updatePickerVisibilityHandler = updatePickerVisibilityHandler;
        _removeHandler = removeHandler;
        _frontendBaseUrl = config["Frontend:BaseUrl"] ?? "http://localhost:4200";
    }

    /// <summary>List all users with access to this app.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<AppUserResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromRoute] Guid appId, CancellationToken ct)
    {
        var users = await _listHandler.HandleAsync(new ListAppUsersQuery(appId), ct);
        var response = users.Select(u => new AppUserResponse
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
        }).ToList();
        return Ok(new ApiResponse<IReadOnlyList<AppUserResponse>>(response));
    }

    /// <summary>Export all users of this app to CSV.</summary>
    [HttpGet("export")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> Export([FromRoute] Guid appId, CancellationToken ct)
    {
        var users = await _listHandler.HandleAsync(new ListAppUsersQuery(appId), ct);

        var csvBuilder = new System.Text.StringBuilder();
        csvBuilder.AppendLine("Name,Email,AppRole,AddedOn");

        foreach (var user in users)
        {
            csvBuilder.AppendLine(string.Join(",",
                EscapeCsvField(user.UserName),
                EscapeCsvField(user.UserEmail),
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
