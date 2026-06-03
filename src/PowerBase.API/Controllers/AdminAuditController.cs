using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.API.Controllers;

[ApiController]
[Route("admin/audit")]
[RequireSuperAdmin]
public class AdminAuditController : ControllerBase
{
    private readonly IAdminRepository _adminRepo;

    public AdminAuditController(IAdminRepository adminRepo)
    {
        _adminRepo = adminRepo;
    }

    [HttpGet("login-attempts")]
    public async Task<IActionResult> ListLoginAttempts(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? email = null,
        CancellationToken ct = default)
    {
        var (items, total) = await _adminRepo.ListLoginAttemptsAsync(page, pageSize, email, ct);
        return Ok(new ApiResponse<object>(new { items, total, page, pageSize }));
    }
}
