using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models.UserTokens;
using PowerBase.Application.UserTokens.Commands.UpdateUserTokenStatus;
using PowerBase.Application.UserTokens.Queries.GetAdminUserTokens;
using PowerBase.Application.UserTokens.Queries.GetSingleTokenDetail;

namespace PowerBase.API.Controllers;

[ApiController]
[Route("api/v1/admin/user-tokens")]
[RequireAuth]
public class AdminUserTokensController : ControllerBase
{
    private readonly GetAdminUserTokensQueryHandler _getAdminTokensHandler;
    private readonly GetSingleTokenDetailQueryHandler _getSingleTokenHandler;
    private readonly UpdateUserTokenStatusCommandHandler _updateStatusHandler;

    public AdminUserTokensController(
        GetAdminUserTokensQueryHandler getAdminTokensHandler,
        GetSingleTokenDetailQueryHandler getSingleTokenHandler,
        UpdateUserTokenStatusCommandHandler updateStatusHandler)
    {
        _getAdminTokensHandler = getAdminTokensHandler;
        _getSingleTokenHandler = getSingleTokenHandler;
        _updateStatusHandler = updateStatusHandler;
    }

    /// <summary>List User Tokens (Admin Console with search, isActive filter, page, pageSize)</summary>
    [HttpGet]
    public async Task<IActionResult> GetAdminTokens(
        [FromQuery] string? search,
        [FromQuery] bool? isActive,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var query = new GetAdminUserTokensQuery
        {
            Search = search,
            IsActive = isActive,
            Page = page,
            PageSize = pageSize
        };

        var result = await _getAdminTokensHandler.HandleAsync(query, ct);
        return Ok(new { data = result.Items, meta = new { total = result.TotalCount, page = result.Page, pageSize = result.PageSize } });
    }

    /// <summary>Get Single Token Detail</summary>
    [HttpGet("{tokenId:guid}")]
    public async Task<IActionResult> GetSingleTokenDetail([FromRoute] Guid tokenId, CancellationToken ct)
    {
        var query = new GetSingleTokenDetailQuery(tokenId);
        var result = await _getSingleTokenHandler.HandleAsync(query, ct);
        if (result == null)
        {
            return NotFound(new { error = new { code = "NOT_FOUND", message = "User Token not found." } });
        }

        return Ok(new { data = result });
    }

    /// <summary>Bulk Update Token Status (Activate/Deactivate)</summary>
    [HttpPatch("status")]
    public async Task<IActionResult> UpdateTokenStatus([FromBody] UpdateUserTokenStatusRequest request, CancellationToken ct)
    {
        var command = new UpdateUserTokenStatusCommand
        {
            PublicIds = request.PublicIds,
            IsActive = request.IsActive
        };

        var result = await _updateStatusHandler.HandleAsync(command, ct);
        return Ok(new { data = result });
    }

    /// <summary>Activate Tokens (Single or Bulk)</summary>
    [HttpPatch("activate")]
    public async Task<IActionResult> ActivateTokens([FromBody] IEnumerable<Guid> publicIds, CancellationToken ct)
    {
        var command = new UpdateUserTokenStatusCommand
        {
            PublicIds = publicIds,
            IsActive = true
        };

        var result = await _updateStatusHandler.HandleAsync(command, ct);
        return Ok(new { data = result });
    }

    /// <summary>Deactivate Tokens (Single or Bulk)</summary>
    [HttpPatch("deactivate")]
    public async Task<IActionResult> DeactivateTokens([FromBody] IEnumerable<Guid> publicIds, CancellationToken ct)
    {
        var command = new UpdateUserTokenStatusCommand
        {
            PublicIds = publicIds,
            IsActive = false
        };

        var result = await _updateStatusHandler.HandleAsync(command, ct);
        return Ok(new { data = result });
    }
}
