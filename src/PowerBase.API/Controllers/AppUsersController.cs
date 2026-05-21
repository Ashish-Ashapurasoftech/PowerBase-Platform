using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.API.Models.Apps;
using PowerBase.Application.Apps.Commands.AddAppUser;
using PowerBase.Application.Apps.Commands.ChangeAppUserRole;
using PowerBase.Application.Apps.Commands.RemoveAppUser;
using PowerBase.Application.Apps.Queries.ListAppUsers;

namespace PowerBase.API.Controllers;

[ApiController]
[Route("apps/{appId:guid}/users")]
[RequireAuth]
public class AppUsersController : ControllerBase
{
    private readonly ListAppUsersQueryHandler _listHandler;
    private readonly AddAppUserCommandHandler _addHandler;
    private readonly ChangeAppUserRoleCommandHandler _changeRoleHandler;
    private readonly RemoveAppUserCommandHandler _removeHandler;

    public AppUsersController(
        ListAppUsersQueryHandler listHandler,
        AddAppUserCommandHandler addHandler,
        ChangeAppUserRoleCommandHandler changeRoleHandler,
        RemoveAppUserCommandHandler removeHandler)
    {
        _listHandler = listHandler;
        _addHandler = addHandler;
        _changeRoleHandler = changeRoleHandler;
        _removeHandler = removeHandler;
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
            AddedOn = u.AddedOn.ToString("o"),
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


    /// <summary>Add a user to this app by email.</summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Add([FromRoute] Guid appId, [FromBody] AddAppUserRequest request, CancellationToken ct)
    {
        await _addHandler.HandleAsync(new AddAppUserCommand(appId, request.Email, request.RolePublicId), ct);
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
