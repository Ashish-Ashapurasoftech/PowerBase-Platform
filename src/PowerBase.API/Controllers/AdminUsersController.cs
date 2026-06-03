using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.Application.Admin.Commands;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.API.Controllers;

[ApiController]
[Route("admin/users")]
[RequireSuperAdmin]
public class AdminUsersController : ControllerBase
{
    private readonly IAdminRepository _adminRepo;
    private readonly ISystemRoleRepository _systemRoleRepo;
    private readonly AdminInvitePlatformUserCommandHandler _invitePlatformHandler;
    private readonly IQueryContext _queryContext;
    private readonly string _frontendBaseUrl;

    public AdminUsersController(
        IAdminRepository adminRepo,
        ISystemRoleRepository systemRoleRepo,
        AdminInvitePlatformUserCommandHandler invitePlatformHandler,
        IQueryContext queryContext,
        IConfiguration config)
    {
        _adminRepo = adminRepo;
        _systemRoleRepo = systemRoleRepo;
        _invitePlatformHandler = invitePlatformHandler;
        _queryContext = queryContext;
        _frontendBaseUrl = config["Frontend:BaseUrl"] ?? "http://localhost:4200";
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? search = null, CancellationToken ct = default)
    {
        var items = await _adminRepo.ListUsersAsync(page, pageSize, search, ct);
        var total = await _adminRepo.CountUsersAsync(search, ct);
        return Ok(new ApiResponse<object>(new { items, total, page, pageSize }));
    }

    [HttpPost("invite")]
    public async Task<IActionResult> InvitePlatformUser([FromBody] InvitePlatformUserRequest request, CancellationToken ct)
    {
        await _invitePlatformHandler.HandleAsync(
            new AdminInvitePlatformUserCommand(request.Email, _frontendBaseUrl, _queryContext.UserId), ct);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<object>(new { }));
    }

    [HttpPatch("{userPublicId:guid}/active")]
    public async Task<IActionResult> SetActive(Guid userPublicId, [FromBody] SetUserActiveRequest request, CancellationToken ct)
    {
        await _adminRepo.UpdateUserActiveStateAsync(userPublicId, request.IsActive, ct);
        return Ok(new ApiResponse<object>(new { }));
    }

    [HttpPatch("{userPublicId:guid}/system-role")]
    public async Task<IActionResult> SetSystemRole(Guid userPublicId, [FromBody] SetUserSystemRoleRequest request, CancellationToken ct)
    {
        int? roleId = null;
        if (!string.IsNullOrEmpty(request.SystemRoleCode))
            roleId = await _systemRoleRepo.GetIdByCodeAsync(request.SystemRoleCode, ct);

        await _adminRepo.UpdateUserSystemRoleAsync(userPublicId, roleId, ct);
        return Ok(new ApiResponse<object>(new { }));
    }
}

// ── Permissions endpoint (separate route prefix) ──────────────────────────────

[ApiController]
[Route("admin/permissions")]
[RequireSuperAdmin]
public class AdminPermissionsController : ControllerBase
{
    private readonly IAdminRepository _adminRepo;

    public AdminPermissionsController(IAdminRepository adminRepo)
    {
        _adminRepo = adminRepo;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var perms = await _adminRepo.ListAllPermissionsAsync(ct);
        return Ok(new ApiResponse<object>(new { items = perms }));
    }
}

public record SetUserActiveRequest(bool IsActive);
public record SetUserSystemRoleRequest(string? SystemRoleCode);
public record InvitePlatformUserRequest(string Email);
