using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.API.Models.Tenants;
using PowerBase.Application.Admin.Commands;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Tenants.Commands.CreateTenant;

namespace PowerBase.API.Controllers;

[ApiController]
[Route("admin/tenants")]
[RequireSuperAdmin]
public class AdminTenantsController : ControllerBase
{
    private readonly IAdminRepository _adminRepo;
    private readonly CreateTenantCommandHandler _createTenantHandler;
    private readonly AdminInviteUserCommandHandler _inviteHandler;
    private readonly IQueryContext _queryContext;
    private readonly string _frontendBaseUrl;

    public AdminTenantsController(
        IAdminRepository adminRepo,
        CreateTenantCommandHandler createTenantHandler,
        AdminInviteUserCommandHandler inviteHandler,
        IQueryContext queryContext,
        IConfiguration config)
    {
        _adminRepo   = adminRepo;
        _createTenantHandler = createTenantHandler;
        _inviteHandler = inviteHandler;
        _queryContext = queryContext;
        _frontendBaseUrl = config["Frontend:BaseUrl"] ?? "http://localhost:4200";
    }

    private async Task<long> ResolveTenantIdAsync(Guid publicId, CancellationToken ct)
    {
        var id = await _adminRepo.GetTenantIdByPublicIdAsync(publicId, ct);
        if (id is null)
            throw new KeyNotFoundException($"Tenant {publicId} not found.");
        return id.Value;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? search = null, CancellationToken ct = default)
    {
        var items = await _adminRepo.ListTenantsAsync(page, pageSize, search, ct);
        var total = await _adminRepo.CountTenantsAsync(search, ct);
        return Ok(new ApiResponse<object>(new { items, total, page, pageSize }));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTenantRequest request, CancellationToken ct)
    {
        var serverConfig = request.ServerConfig is { } sc
            ? new TenantServerConfig(sc.Host, sc.Port, sc.AdminLogin, sc.AdminPassword, sc.Encrypt)
            : null;
        var result = await _createTenantHandler.HandleAsync(new CreateTenantCommand(request.Name, serverConfig), ct);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<object>(new
        {
            publicId = result.TenantPublicId,
            name = result.TenantName,
            slug = result.TenantSlug,
        }));
    }

    [HttpPatch("{tenantPublicId:guid}/status")]
    public async Task<IActionResult> SetStatus(Guid tenantPublicId, [FromBody] SetTenantStatusRequest request, CancellationToken ct)
    {
        var tenantId = await ResolveTenantIdAsync(tenantPublicId, ct);
        await _adminRepo.UpdateTenantStatusAsync(tenantId, request.Status, ct);
        return Ok(new ApiResponse<object>(new { }));
    }

    [HttpGet("{tenantPublicId:guid}/members")]
    public async Task<IActionResult> ListMembers(Guid tenantPublicId, CancellationToken ct)
    {
        var tenantId = await ResolveTenantIdAsync(tenantPublicId, ct);
        var members = await _adminRepo.ListTenantMembersAsync(tenantId, ct);
        return Ok(new ApiResponse<object>(new { items = members }));
    }

    [HttpGet("{tenantPublicId:guid}/roles")]
    public async Task<IActionResult> ListRoles(Guid tenantPublicId, CancellationToken ct)
    {
        var tenantId = await ResolveTenantIdAsync(tenantPublicId, ct);
        var roles = await _adminRepo.ListTenantRolesAsync(tenantId, ct);
        return Ok(new ApiResponse<object>(new { items = roles }));
    }

    [HttpGet("{tenantPublicId:guid}/assignable-users")]
    public async Task<IActionResult> ListAssignableUsers(Guid tenantPublicId, [FromQuery] string? search = null, CancellationToken ct = default)
    {
        var tenantId = await ResolveTenantIdAsync(tenantPublicId, ct);
        var users = await _adminRepo.ListUsersNotInTenantAsync(tenantId, search, ct);
        return Ok(new ApiResponse<object>(new { items = users }));
    }

    [HttpPost("{tenantPublicId:guid}/members")]
    public async Task<IActionResult> AssignMember(Guid tenantPublicId, [FromBody] AssignTenantMemberRequest request, CancellationToken ct)
    {
        var tenantId = await ResolveTenantIdAsync(tenantPublicId, ct);

        // Guard: cannot assign to inactive tenant
        var status = await _adminRepo.GetTenantStatusAsync(tenantId, ct);
        if (status != "Active")
            return Conflict(new { error = new { code = "TENANT_INACTIVE", message = "Cannot assign members to an inactive tenant." } });

        var userId = await _adminRepo.GetUserIdByEmailAsync(request.Email, ct);
        if (userId is null)
            return NotFound(new { error = new { code = "NOT_FOUND", message = $"User '{request.Email}' not found." } });

        var roleId = await _adminRepo.GetTenantRoleIdByPublicIdAsync(tenantId, request.RolePublicId, ct);
        if (roleId is null)
            return NotFound(new { error = new { code = "NOT_FOUND", message = "Role not found." } });

        await _adminRepo.AssignUserToTenantAsync(tenantId, userId.Value, roleId.Value, _queryContext.UserId, ct: ct);
        return Ok(new ApiResponse<object>(new { }));
    }

    [HttpPost("{tenantPublicId:guid}/members/invite")]
    public async Task<IActionResult> InviteMember(Guid tenantPublicId, [FromBody] InviteTenantMemberRequest request, CancellationToken ct)
    {
        var tenantId = await ResolveTenantIdAsync(tenantPublicId, ct);

        var status = await _adminRepo.GetTenantStatusAsync(tenantId, ct);
        if (status != "Active")
            return Conflict(new { error = new { code = "TENANT_INACTIVE", message = "Cannot invite members to an inactive tenant." } });

        await _inviteHandler.HandleAsync(new AdminInviteUserCommand(
            tenantId, request.Email, request.RolePublicId, _frontendBaseUrl, _queryContext.UserId), ct);

        return StatusCode(StatusCodes.Status201Created, new ApiResponse<object>(new { }));
    }

    [HttpPatch("{tenantPublicId:guid}/members/{userPublicId:guid}/role")]
    public async Task<IActionResult> ChangeMemberRole(Guid tenantPublicId, Guid userPublicId, [FromBody] ChangeMemberRoleRequest request, CancellationToken ct)
    {
        var tenantId = await ResolveTenantIdAsync(tenantPublicId, ct);
        var roleId = await _adminRepo.GetTenantRoleIdByPublicIdAsync(tenantId, request.RolePublicId, ct);
        if (roleId is null)
            return NotFound(new { error = new { code = "NOT_FOUND", message = "Role not found." } });

        await _adminRepo.ChangeMemberRoleAsync(tenantId, userPublicId, roleId.Value, ct);
        return Ok(new ApiResponse<object>(new { }));
    }

    [HttpDelete("{tenantPublicId:guid}/members/{userPublicId:guid}")]
    public async Task<IActionResult> RemoveMember(Guid tenantPublicId, Guid userPublicId, CancellationToken ct)
    {
        var tenantId = await ResolveTenantIdAsync(tenantPublicId, ct);
        await _adminRepo.RemoveUserFromTenantAsync(tenantId, userPublicId, ct);
        return Ok(new ApiResponse<object>(new { }));
    }

    [HttpGet("{tenantPublicId:guid}/roles/{rolePublicId:guid}/permissions")]
    public async Task<IActionResult> GetRolePermissions(Guid tenantPublicId, Guid rolePublicId, CancellationToken ct)
    {
        var tenantId = await ResolveTenantIdAsync(tenantPublicId, ct);
        var perms = await _adminRepo.GetTenantRolePermissionsAsync(tenantId, rolePublicId, ct);
        return Ok(new ApiResponse<object>(new { items = perms }));
    }

    [HttpPut("{tenantPublicId:guid}/roles/{rolePublicId:guid}/permissions")]
    public async Task<IActionResult> UpdateRolePermissions(Guid tenantPublicId, Guid rolePublicId, [FromBody] UpdateRolePermissionsAdminRequest request, CancellationToken ct)
    {
        var tenantId = await ResolveTenantIdAsync(tenantPublicId, ct);
        await _adminRepo.ReplaceRolePermissionsAdminAsync(tenantId, rolePublicId, request.PermissionCodes, ct);
        return Ok(new ApiResponse<object>(new { }));
    }
}

public record SetTenantStatusRequest(string Status);
public record AssignTenantMemberRequest(string Email, Guid RolePublicId);
public record InviteTenantMemberRequest(string Email, Guid RolePublicId);
public record ChangeMemberRoleRequest(Guid RolePublicId);
public record UpdateRolePermissionsAdminRequest(List<string> PermissionCodes);
