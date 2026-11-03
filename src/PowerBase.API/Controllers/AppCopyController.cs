using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.API.Models.Apps;
using PowerBase.Application.Apps.Commands.CopyApp;
using PowerBase.Application.Apps.Queries.GetAppCopyProgress;
using PowerBase.Domain.Constants;

namespace PowerBase.API.Controllers;

[ApiController]
[Route("apps/{appId:guid}/copy")]
public class AppCopyController : ControllerBase
{
    private readonly CopyAppCommandHandler _copyHandler;

    public AppCopyController(CopyAppCommandHandler copyHandler)
    {
        _copyHandler = copyHandler;
    }

    /// <summary>Copy an app without its data (Copy App – Schema Only): tables, fields, relationships, forms, rules,
    /// reports, roles and permissions, pages and saved imports (schedule off); PowerFlows are not copied. Needs the right to
    /// create apps, and in the original app the Administrator role or "Allow non-administrators to copy".</summary>
    [HttpPost]
    [RequirePermission(PermissionCodes.AppsCreate)]
    [ProducesResponseType(typeof(ApiResponse<CopyAppResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Copy([FromRoute] Guid appId, [FromBody] CopyAppRequest request, CancellationToken ct)
    {
        var result = await _copyHandler.HandleAsync(
            new CopyAppCommand(appId, request.Name, request.Description, request.CopyUsers, request.ProgressId), ct);

        var response = new CopyAppResponse
        {
            PublicId = result.PublicId,
            Name = result.Name,
            Description = result.Description,
            IsEncrypted = result.IsEncrypted,
            Warnings = result.Warnings.Select(w => new CopyAppWarningResponse { Code = w.Code, Message = w.Message }).ToList()
        };
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<CopyAppResponse>(response));
    }

    /// <summary>How far the caller's running copy is (the progressId it sent with the copy), for the dialog's progress bar.
    /// Data is null when this server knows no such copy for the caller: not started yet, ended long ago, or run on another
    /// server instance. Polled while the copy request is open.</summary>
    [HttpGet("progress/{progressId:guid}")]
    [RequirePermission(PermissionCodes.AppsCreate)]
    [ProducesResponseType(typeof(ApiResponse<AppCopyProgressResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult Progress([FromRoute] Guid appId, [FromRoute] Guid progressId, [FromServices] GetAppCopyProgressQueryHandler handler)
    {
        var progress = handler.Handle(progressId);
        var response = progress is null ? null : new AppCopyProgressResponse
        {
            Percent = progress.Percent,
            Stage = progress.Stage,
            IsDone = progress.IsDone,
            IsFailed = progress.IsFailed
        };
        return Ok(new ApiResponse<AppCopyProgressResponse?>(response));
    }
}
