using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models.AppTokens;
using PowerBase.Application.AppTokens.Commands.BulkDeleteAppTokens;
using PowerBase.Application.AppTokens.Commands.CreateAppToken;
using PowerBase.Application.AppTokens.Commands.DeleteAppToken;
using PowerBase.Application.AppTokens.Commands.RotateAppToken;
using PowerBase.Application.AppTokens.Commands.UpdateAppTokenStatus;
using PowerBase.Application.AppTokens.Queries.GetAppTokens;

namespace PowerBase.API.Controllers;

[ApiController]
[Route("api/v1/apps/{appPublicId:guid}/tokens")]
[RequireAuth]
public class AppTokensController : ControllerBase
{
    private readonly CreateAppTokenCommandHandler _createHandler;
    private readonly GetAppTokensQueryHandler _getAppTokensHandler;
    private readonly UpdateAppTokenStatusCommandHandler _updateStatusHandler;
    private readonly RotateAppTokenCommandHandler _rotateHandler;
    private readonly DeleteAppTokenCommandHandler _deleteHandler;
    private readonly BulkDeleteAppTokensCommandHandler _bulkDeleteHandler;

    public AppTokensController(
        CreateAppTokenCommandHandler createHandler,
        GetAppTokensQueryHandler getAppTokensHandler,
        UpdateAppTokenStatusCommandHandler updateStatusHandler,
        RotateAppTokenCommandHandler rotateHandler,
        DeleteAppTokenCommandHandler deleteHandler,
        BulkDeleteAppTokensCommandHandler bulkDeleteHandler)
    {
        _createHandler = createHandler;
        _getAppTokensHandler = getAppTokensHandler;
        _updateStatusHandler = updateStatusHandler;
        _rotateHandler = rotateHandler;
        _deleteHandler = deleteHandler;
        _bulkDeleteHandler = bulkDeleteHandler;
    }

    /// <summary>Create an App Token for a specific App</summary>
    [HttpPost]
    public async Task<IActionResult> CreateToken([FromRoute] Guid appPublicId, [FromBody] CreateAppTokenRequest request, CancellationToken ct)
    {
        var command = new CreateAppTokenCommand
        {
            AppPublicId = appPublicId,
            TokenName = request.TokenName,
            Description = request.Description
        };

        var result = await _createHandler.HandleAsync(command, ct);
        return Ok(new { data = result });
    }

    /// <summary>Get paged App Tokens for a specific App</summary>
    [HttpGet]
    public async Task<IActionResult> GetAppTokens(
        [FromRoute] Guid appPublicId,
        [FromQuery] string? search,
        [FromQuery] bool? isActive,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var query = new GetAppTokensQuery
        {
            AppPublicId = appPublicId,
            Search = search,
            IsActive = isActive,
            Page = page,
            PageSize = pageSize
        };

        var result = await _getAppTokensHandler.HandleAsync(query, ct);
        return Ok(new { data = result.Items, items = result.Items, totalCount = result.TotalCount, page = result.Page, pageSize = result.PageSize });
    }

    /// <summary>Update status (Activate/Deactivate) of an App Token</summary>
    [HttpPut("{publicId:guid}/status")]
    public async Task<IActionResult> UpdateStatus(
        [FromRoute] Guid appPublicId,
        [FromRoute] Guid publicId,
        [FromBody] UpdateAppTokenStatusRequest request,
        CancellationToken ct)
    {
        var command = new UpdateAppTokenStatusCommand
        {
            AppPublicId = appPublicId,
            PublicId = publicId,
            IsActive = request.IsActive
        };

        await _updateStatusHandler.HandleAsync(command, ct);
        return Ok(new { success = true });
    }

    /// <summary>Rotate secret key of an App Token</summary>
    [HttpPost("{publicId:guid}/rotate")]
    public async Task<IActionResult> RotateToken(
        [FromRoute] Guid appPublicId,
        [FromRoute] Guid publicId,
        CancellationToken ct)
    {
        var result = await _rotateHandler.HandleAsync(appPublicId, publicId, ct);
        return Ok(new { data = result });
    }

    /// <summary>Delete an App Token</summary>
    [HttpDelete("{publicId:guid}")]
    public async Task<IActionResult> DeleteToken(
        [FromRoute] Guid appPublicId,
        [FromRoute] Guid publicId,
        CancellationToken ct)
    {
        await _deleteHandler.HandleAsync(appPublicId, publicId, ct);
        return Ok(new { success = true });
    }

    /// <summary>Bulk-delete multiple App Tokens by their public IDs in a single request.</summary>
    [HttpPost("bulk-delete")]
    public async Task<IActionResult> BulkDeleteTokens(
        [FromRoute] Guid appPublicId,
        [FromBody] BulkDeleteAppTokensRequest request,
        CancellationToken ct)
    {
        var deletedCount = await _bulkDeleteHandler.HandleAsync(new BulkDeleteAppTokensCommand(appPublicId, request.PublicIds), ct);
        return Ok(new { success = true, deletedCount });
    }
}
