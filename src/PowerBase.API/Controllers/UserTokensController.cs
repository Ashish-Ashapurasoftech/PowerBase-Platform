using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models.UserTokens;
using PowerBase.Application.UserTokens.Commands.CreateUserToken;
using PowerBase.Application.UserTokens.Commands.RevokeUserToken;
using PowerBase.Application.UserTokens.Commands.RotateUserToken;
using PowerBase.Application.UserTokens.Commands.UpdateUserToken;
using PowerBase.Application.UserTokens.Queries.GetMyUserTokens;

namespace PowerBase.API.Controllers;

[ApiController]
[Route("api/v1/user-tokens")]
[RequireAuth]
public class UserTokensController : ControllerBase
{
    private readonly CreateUserTokenCommandHandler _createHandler;
    private readonly GetMyUserTokensQueryHandler _getMyTokensHandler;
    private readonly RevokeUserTokenCommandHandler _revokeHandler;
    private readonly RotateUserTokenCommandHandler _rotateHandler;
    private readonly UpdateUserTokenCommandHandler _updateHandler;

    public UserTokensController(
        CreateUserTokenCommandHandler createHandler,
        GetMyUserTokensQueryHandler getMyTokensHandler,
        RevokeUserTokenCommandHandler revokeHandler,
        RotateUserTokenCommandHandler rotateHandler,
        UpdateUserTokenCommandHandler updateHandler)
    {
        _createHandler = createHandler;
        _getMyTokensHandler = getMyTokensHandler;
        _revokeHandler = revokeHandler;
        _rotateHandler = rotateHandler;
        _updateHandler = updateHandler;
    }

    /// <summary>Create a User Token (Self-service, permission-gated)</summary>
    [HttpPost]
    [RequirePermission("token:create")]
    public async Task<IActionResult> CreateToken([FromBody] CreateUserTokenRequest request, CancellationToken ct)
    {
        var command = new CreateUserTokenCommand
        {
            TokenName = request.TokenName,
            Description = request.Description,
            AccessAllApps = request.AccessAllApps,
            AllowedAppPublicIds = request.AllowedAppPublicIds
        };

        var result = await _createHandler.HandleAsync(command, ct);
        return Ok(new { data = result });
    }

    [HttpGet]
    public async Task<IActionResult> GetMyTokens(CancellationToken ct)
    {
        var query = new GetMyUserTokensQuery();
        var result = await _getMyTokensHandler.HandleAsync(query, ct);
        return Ok(new { data = result });
    }

    /// <summary>Rotate a Token</summary>
    [HttpPost("{publicId:guid}/rotate")]
    public async Task<IActionResult> RotateToken([FromRoute] Guid publicId, CancellationToken ct)
    {
        var command = new RotateUserTokenCommand(publicId);
        var result = await _rotateHandler.HandleAsync(command, ct);
        return Ok(new { data = result });
    }

    [HttpDelete("{publicId:guid}")]
    public async Task<IActionResult> RevokeToken([FromRoute] Guid publicId, CancellationToken ct)
    {
        var command = new RevokeUserTokenCommand(publicId);
        var result = await _revokeHandler.HandleAsync(command, ct);
        return Ok(new { data = result });
    }

    [HttpPut("{publicId:guid}")]
    public async Task<IActionResult> UpdateToken([FromRoute] Guid publicId, [FromBody] UpdateUserTokenRequest request, CancellationToken ct)
    {
        var command = new UpdateUserTokenCommand
        {
            PublicId = publicId,
            TokenName = request.TokenName,
            Description = request.Description,
            AccessAllApps = request.AccessAllApps,
            AllowedAppPublicIds = request.AllowedAppPublicIds
        };

        var result = await _updateHandler.HandleAsync(command, ct);
        return Ok(new { data = result });
    }
}

