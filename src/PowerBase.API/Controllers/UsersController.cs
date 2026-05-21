using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.API.Models.Users;
using PowerBase.Application.Users;
using PowerBase.Application.Users.Commands.ChangeUserRole;
using PowerBase.Application.Users.Commands.InviteUser;
using PowerBase.Application.Users.Commands.RemoveUser;
using PowerBase.Application.Users.Queries.ListUsers;
using PowerBase.Domain.Constants;

namespace PowerBase.API.Controllers;

[ApiController]
[Route("tenants/users")]
public class UsersController : ControllerBase
{
    private readonly ListUsersQueryHandler _listHandler;
    private readonly InviteUserCommandHandler _inviteHandler;
    private readonly ChangeUserRoleCommandHandler _changeRoleHandler;
    private readonly RemoveUserCommandHandler _removeHandler;
    private readonly string _frontendBaseUrl;

    public UsersController(
        ListUsersQueryHandler listHandler,
        InviteUserCommandHandler inviteHandler,
        ChangeUserRoleCommandHandler changeRoleHandler,
        RemoveUserCommandHandler removeHandler,
        IConfiguration config)
    {
        _listHandler = listHandler;
        _inviteHandler = inviteHandler;
        _changeRoleHandler = changeRoleHandler;
        _removeHandler = removeHandler;
        _frontendBaseUrl = config["Frontend:BaseUrl"] ?? "http://localhost:4200";
    }

    /// <summary>List all users in the current tenant.</summary>
    [HttpGet]
    [RequirePermission(PermissionCodes.UsersManage)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<TenantUserResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List([FromQuery] string? searchTerm, [FromQuery] string? roleName, [FromQuery] string? status, CancellationToken ct)
    {
        var users = await _listHandler.HandleAsync(new ListUsersQuery(searchTerm, roleName, status), ct);
        var items = users.Select(MapToResponse).ToList();
        return Ok(new ApiResponse<IReadOnlyList<TenantUserResponse>>(items));
    }

    /// <summary>Export all users in the current tenant to CSV.</summary>
    [HttpGet("export")]
    [RequirePermission(PermissionCodes.UsersManage)]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Export([FromQuery] string? searchTerm, [FromQuery] string? roleName, [FromQuery] string? status, CancellationToken ct)
    {
        var users = await _listHandler.HandleAsync(new ListUsersQuery(searchTerm, roleName, status), ct);

        var csvBuilder = new System.Text.StringBuilder();
        csvBuilder.AppendLine("Name,Email,Role,IsOwner,Status,JoinedOn");

        foreach (var user in users)
        {
            csvBuilder.AppendLine(string.Join(",",
                EscapeCsvField(user.Name),
                EscapeCsvField(user.Email),
                EscapeCsvField(user.RoleName),
                EscapeCsvField(user.IsOwner ? "Yes" : "No"),
                EscapeCsvField(user.IsActive ? "Active" : "Pending"),
                EscapeCsvField(user.JoinedOn.ToString("yyyy-MM-dd HH:mm:ss"))
            ));
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(csvBuilder.ToString());
        return File(bytes, "text/csv", "tenant_users.csv");
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

    /// <summary>Invite an existing user to the current tenant.</summary>
    [HttpPost("invite")]
    [RequirePermission(PermissionCodes.UsersInvite)]
    [ProducesResponseType(typeof(ApiResponse<TenantUserResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Invite([FromBody] InviteUserRequest request, CancellationToken ct)
    {
        var result = await _inviteHandler.HandleAsync(new InviteUserCommand(request.Email, request.RolePublicId, _frontendBaseUrl), ct);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<TenantUserResponse>(MapToResponse(result)));
    }

    /// <summary>Change the role assigned to a user in the current tenant.</summary>
    [HttpPatch("{userPublicId:guid}")]
    [RequirePermission(PermissionCodes.UsersManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ChangeRole(Guid userPublicId, [FromBody] ChangeUserRoleRequest request, CancellationToken ct)
    {
        await _changeRoleHandler.HandleAsync(new ChangeUserRoleCommand(userPublicId, request.RolePublicId), ct);
        return NoContent();
    }

    /// <summary>Remove a user from the current tenant.</summary>
    [HttpDelete("{userPublicId:guid}")]
    [RequirePermission(PermissionCodes.UsersManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Remove(Guid userPublicId, CancellationToken ct)
    {
        await _removeHandler.HandleAsync(new RemoveUserCommand(userPublicId), ct);
        return NoContent();
    }

    private static TenantUserResponse MapToResponse(TenantUserDetail d) => new()
    {
        UserPublicId = d.UserPublicId,
        Email = d.Email,
        Name = d.Name,
        RolePublicId = d.RolePublicId,
        RoleName = d.RoleName,
        IsOwner = d.IsOwner,
        IsActive = d.IsActive,
        JoinedOn = d.JoinedOn,
    };
}
