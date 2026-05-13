using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.API.Models.Auth;
using PowerBase.Application.Auth.Commands.Signup;
using PowerBase.Application.Auth.Queries.GetMe;
using PowerBase.Application.Auth.Queries.Login;

namespace PowerBase.API.Controllers;

[ApiController]
[Route("auth")]
public class AuthController : ControllerBase
{
    private readonly SignupCommandHandler _signupHandler;
    private readonly LoginQueryHandler _loginHandler;
    private readonly GetMeQueryHandler _getMeHandler;

    public AuthController(
        SignupCommandHandler signupHandler,
        LoginQueryHandler loginHandler,
        GetMeQueryHandler getMeHandler)
    {
        _signupHandler = signupHandler;
        _loginHandler = loginHandler;
        _getMeHandler = getMeHandler;
    }

    /// <summary>Create a new user account and tenant. Returns a JWT on success.</summary>
    [HttpPost("signup")]
    [ProducesResponseType(typeof(ApiResponse<AuthResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Signup([FromBody] SignupRequest request, CancellationToken ct)
    {
        var command = new SignupCommand(request.Email, request.Password, request.FirstName, request.LastName, request.TenantName);
        var result = await _signupHandler.HandleAsync(command, ct);
        var response = MapToAuthResponse(result.Token, result.ExpiresAt, result.UserPublicId, result.Email, result.FirstName, result.LastName);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<AuthResponse>(response));
    }

    /// <summary>Authenticate with email and password. Returns a JWT on success.</summary>
    [HttpPost("login")]
    [ProducesResponseType(typeof(ApiResponse<AuthResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
        var query = new LoginQuery(request.Email, request.Password, ipAddress);
        var result = await _loginHandler.HandleAsync(query, ct);
        var response = MapToAuthResponse(result.Token, result.ExpiresAt, result.UserPublicId, result.Email, result.FirstName, result.LastName);
        return Ok(new ApiResponse<AuthResponse>(response));
    }

    /// <summary>Get the currently authenticated user's profile.</summary>
    [HttpGet("me")]
    [RequireAuth]
    [ProducesResponseType(typeof(ApiResponse<UserResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var user = await _getMeHandler.HandleAsync(new GetMeQuery(), ct);
        var response = new UserResponse
        {
            PublicId = user.PublicId,
            Email = user.Email,
            FirstName = user.FirstName,
            LastName = user.LastName,
        };
        return Ok(new ApiResponse<UserResponse>(response));
    }

    private static AuthResponse MapToAuthResponse(string token, DateTime expiresAt, Guid publicId, string email, string firstName, string lastName)
        => new()
        {
            Token = token,
            ExpiresAt = expiresAt.ToString("o"),
            User = new UserResponse
            {
                PublicId = publicId,
                Email = email,
                FirstName = firstName,
                LastName = lastName,
            }
        };
}
