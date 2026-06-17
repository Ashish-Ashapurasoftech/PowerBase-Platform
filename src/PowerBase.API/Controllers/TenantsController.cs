using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.API.Models.Auth;
using PowerBase.API.Models.Tenants;
using PowerBase.Application.Tenants.Commands.CreateTenant;

namespace PowerBase.API.Controllers;

[ApiController]
[Route("tenants")]
public class TenantsController : ControllerBase
{
    private readonly CreateTenantCommandHandler _createTenantHandler;

    public TenantsController(CreateTenantCommandHandler createTenantHandler)
    {
        _createTenantHandler = createTenantHandler;
    }

    /// <summary>Create a new tenant for the authenticated user. Returns a tenant-scoped JWT.</summary>
    [HttpPost]
    [RequireAuth]
    [ProducesResponseType(typeof(ApiResponse<AuthResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Create([FromBody] CreateTenantRequest request, CancellationToken ct)
    {
        var serverConfig = request.ServerConfig is { } sc
            ? new TenantServerConfig(sc.Host, sc.Port, sc.AdminLogin, sc.AdminPassword, sc.Encrypt)
            : null;
        var result = await _createTenantHandler.HandleAsync(new CreateTenantCommand(request.Name, serverConfig), ct);
        var response = new AuthResponse
        {
            Token = result.Token,
            ExpiresAt = result.ExpiresAt.ToString("o"),
            User = new UserResponse { PublicId = result.UserPublicId, Email = result.Email, Name = result.Name },
            TenantPublicId = result.TenantPublicId,
            TenantName = result.TenantName,
        };
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<AuthResponse>(response));
    }
}
