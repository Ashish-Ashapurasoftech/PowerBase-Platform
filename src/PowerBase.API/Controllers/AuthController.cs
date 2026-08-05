using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.API.Models.Auth;
using PowerBase.Application.Auth.Commands.AcceptInvite;
using PowerBase.Application.Auth.Commands.SelectTenant;
using PowerBase.Application.Auth.Commands.RefreshToken;
using PowerBase.Application.Auth.Commands.Signup;
using PowerBase.Application.Auth.Commands.UpdateMyPreferences;
using PowerBase.Application.Auth.Queries.GetMe;
using PowerBase.Application.Auth.Queries.GetMyPreferences;
using PowerBase.Application.Auth.Queries.Login;
using PowerBase.Application.Auth.Commands.ResetPassword;
using PowerBase.Application.Auth.Commands.ForgotPassword;
using PowerBase.Application.Common.Interfaces;
using System.Text.Json;
using PowerBase.Domain.ValueObjects;

namespace PowerBase.API.Controllers;

[ApiController]
[Route("auth")]
public class AuthController : ControllerBase
{
    private readonly SignupCommandHandler _signupHandler;
    private readonly LoginQueryHandler _loginHandler;
    private readonly GetMeQueryHandler _getMeHandler;
    private readonly SelectTenantCommandHandler _selectTenantHandler;
    private readonly AcceptInviteCommandHandler _acceptInviteHandler;
    private readonly ForgotPasswordCommandHandler _forgotPasswordHandler;
    private readonly ResetPasswordCommandHandler _resetPasswordHandler;
    private readonly RefreshTokenCommandHandler _refreshTokenHandler;
    private readonly GetMyPreferencesQueryHandler _getMyPreferencesHandler;
    private readonly UpdateMyPreferencesCommandHandler _updateMyPreferencesHandler;

    public AuthController(
        SignupCommandHandler signupHandler,
        LoginQueryHandler loginHandler,
        GetMeQueryHandler getMeHandler,
        SelectTenantCommandHandler selectTenantHandler,
        AcceptInviteCommandHandler acceptInviteHandler,
        ForgotPasswordCommandHandler forgotPasswordHandler,
        ResetPasswordCommandHandler resetPasswordHandler,
        RefreshTokenCommandHandler refreshTokenHandler,
        GetMyPreferencesQueryHandler getMyPreferencesHandler,
        UpdateMyPreferencesCommandHandler updateMyPreferencesHandler)
    {
        _signupHandler = signupHandler;
        _loginHandler = loginHandler;
        _getMeHandler = getMeHandler;
        _selectTenantHandler = selectTenantHandler;
        _acceptInviteHandler = acceptInviteHandler;
        _forgotPasswordHandler = forgotPasswordHandler;
        _resetPasswordHandler = resetPasswordHandler;
        _refreshTokenHandler = refreshTokenHandler;
        _getMyPreferencesHandler = getMyPreferencesHandler;
        _updateMyPreferencesHandler = updateMyPreferencesHandler;
    }

    private static readonly JsonSerializerOptions PreferencesJsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Get the current user's personal appearance/grid preferences — overrides
    /// that apply only to this account, on top of the active app's Branding defaults.</summary>
    [HttpGet("me/preferences")]
    [RequireAuth]
    [ProducesResponseType(typeof(ApiResponse<UserPreferencesSettings>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetMyPreferences(CancellationToken ct)
    {
        var user = await _getMyPreferencesHandler.HandleAsync(new GetMyPreferencesQuery(), ct);
        var preferences = string.IsNullOrEmpty(user.Preferences)
            ? new UserPreferencesSettings()
            : JsonSerializer.Deserialize<UserPreferencesSettings>(user.Preferences, PreferencesJsonOptions) ?? new UserPreferencesSettings();
        return Ok(new ApiResponse<UserPreferencesSettings>(preferences));
    }

    /// <summary>Replace the current user's personal appearance/grid preferences. Any field
    /// left null falls back to inheriting the active app's Branding default.</summary>
    [HttpPut("me/preferences")]
    [RequireAuth]
    [ProducesResponseType(typeof(ApiResponse<UserPreferencesSettings>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateMyPreferences([FromBody] UserPreferencesSettings request, CancellationToken ct)
    {
        await _updateMyPreferencesHandler.HandleAsync(new UpdateMyPreferencesCommand(request), ct);
        return await GetMyPreferences(ct);
    }

    /// <summary>Register a new user account. Returns an identity token (no tenant context yet).</summary>
    [HttpPost("signup")]
    [ProducesResponseType(typeof(ApiResponse<IdentityResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Signup([FromBody] SignupRequest request, CancellationToken ct)
    {
        var command = new SignupCommand(request.Email, request.Password, request.Name);
        var result = await _signupHandler.HandleAsync(command, ct);
        var response = new IdentityResponse
        {
            IdentityToken = result.IdentityToken,
            ExpiresAt = result.ExpiresAt.ToString("o"),
            User = new UserResponse { PublicId = result.UserPublicId, Email = result.Email, Name = result.Name },
            Tenants = [],
        };
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<IdentityResponse>(response));
    }

    /// <summary>Authenticate with email and password. Returns an identity token and the user's tenant list.</summary>
    [HttpPost("login")]
    [ProducesResponseType(typeof(ApiResponse<IdentityResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
        var result = await _loginHandler.HandleAsync(new LoginQuery(request.Email, request.Password, ipAddress), ct);
        var response = new IdentityResponse
        {
            IdentityToken = result.IdentityToken,
            ExpiresAt = result.ExpiresAt.ToString("o"),
            User = new UserResponse { PublicId = result.UserPublicId, Email = result.Email, Name = result.Name },
            Tenants = result.Tenants.Select(t => new TenantListItem
            {
                PublicId = t.PublicId,
                Name = t.Name,
                Slug = t.Slug,
                IsOwner = t.IsOwner,
            }).ToList(),
        };
        return Ok(new ApiResponse<IdentityResponse>(response));
    }

    /// <summary>Exchange an identity token for a tenant-scoped JWT. Send the identity token as Bearer.</summary>
    [HttpPost("select-tenant")]
    [RequireAuth]
    [ProducesResponseType(typeof(ApiResponse<AuthResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SelectTenant([FromBody] SelectTenantRequest request, CancellationToken ct)
    {
        var result = await _selectTenantHandler.HandleAsync(new SelectTenantCommand(request.TenantPublicId), ct);
        return Ok(new ApiResponse<AuthResponse>(MapToAuthResponse(result.Token, result.ExpiresAt, result.UserPublicId,
            result.Email, result.Name, result.TenantPublicId, result.TenantName)));
    }

    /// <summary>Refresh the current tenant token.</summary>
    [HttpPost("refresh")]
    [RequireAuth]
    [ProducesResponseType(typeof(ApiResponse<AuthResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        var result = await _refreshTokenHandler.HandleAsync(new RefreshTokenCommand(), ct);
        return Ok(new ApiResponse<AuthResponse>(MapToAuthResponse(result.Token, result.ExpiresAt, result.UserPublicId,
            result.Email, result.Name, result.TenantPublicId, result.TenantName)));
    }

    /// <summary>Accept an invitation and complete account setup (name + password). Returns an identity token.</summary>
    [HttpPost("accept-invite")]
    [ProducesResponseType(typeof(ApiResponse<IdentityResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AcceptInvite([FromBody] AcceptInviteRequest request, CancellationToken ct)
    {
        var result = await _acceptInviteHandler.HandleAsync(
            new AcceptInviteCommand(request.Token, request.Name, request.Password), ct);
        var response = new IdentityResponse
        {
            IdentityToken = result.IdentityToken,
            ExpiresAt = result.ExpiresAt.ToString("o"),
            User = new UserResponse { PublicId = result.UserPublicId, Email = result.Email, Name = result.Name },
            Tenants = result.Tenants.Select(t => new TenantListItem
            {
                PublicId = t.PublicId,
                Name = t.Name,
                Slug = t.Slug,
                IsOwner = t.IsOwner,
            }).ToList(),
        };
        return Ok(new ApiResponse<IdentityResponse>(response));
    }

    /// <summary>Get the currently authenticated user's profile. Works with both identity and tenant tokens.</summary>
    [HttpGet("me")]
    [RequireAuth]
    [ProducesResponseType(typeof(ApiResponse<UserResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var user = await _getMeHandler.HandleAsync(new GetMeQuery(), ct);
        return Ok(new ApiResponse<UserResponse>(new UserResponse
        {
            PublicId = user.PublicId,
            Email = user.Email,
            Name = user.Name,
        }));
    }

    /// <summary>Get the current user's active permissions for the selected tenant context.</summary>
    [HttpGet("permissions")]
    [RequireAuth]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlySet<string>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult GetPermissions([FromServices] IQueryContext queryContext)
    {
        return Ok(new ApiResponse<IReadOnlySet<string>>(queryContext.Permissions));
    }

    [HttpPost("forgot-password")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request, CancellationToken ct)
    {
        var baseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
        // It's usually better to use a configured frontend URL if the backend and frontend are hosted separately.
        // For Powerbase, we'll try to get it from Origin or referer headers if possible, or fallback to config
        var origin = Request.Headers["Origin"].ToString();
        var appBaseUrl = !string.IsNullOrEmpty(origin) ? origin : baseUrl;

        var command = new ForgotPasswordCommand(request.Email);
        await _forgotPasswordHandler.HandleAsync(command, appBaseUrl, ct);
        // Always return OK so as not to reveal whether an email exists
        return Ok(new { Message = "If that email address exists in our system, you will receive a password reset link shortly." });
    }

    [HttpPost("reset-password")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request, CancellationToken ct)
    {
        var command = new ResetPasswordCommand(request.Token, request.NewPassword);
        await _resetPasswordHandler.HandleAsync(command, ct);
        return Ok(new { Message = "Password has been reset successfully." });
    }

    public record ForgotPasswordRequest(string Email);
    public record ResetPasswordRequest(string Token, string NewPassword);

    private static AuthResponse MapToAuthResponse(string token, DateTime expiresAt, Guid publicId,
        string email, string name, Guid tenantPublicId, string tenantName)
        => new()
        {
            Token = token,
            ExpiresAt = expiresAt.ToString("o"),
            User = new UserResponse { PublicId = publicId, Email = email, Name = name },
            TenantPublicId = tenantPublicId,
            TenantName = tenantName,
        };
}
