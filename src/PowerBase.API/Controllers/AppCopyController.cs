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

    /// <summary>Copy an app: tables, fields, relationships, forms, rules, reports, roles and permissions, pages and saved
    /// imports (schedule off); PowerFlows are not copied. With <c>withData</c> the records come along too (new record ids,
    /// references pointing at the copied records, and unless <c>includeFiles</c> is false a copy of every file attachment
    /// and its older revisions). Needs the
    /// tenant permission to copy apps (apps:copy), and in the original app the Administrator role or "Allow
    /// non-administrators to copy"; a copy with data needs the Administrator role.</summary>
    [HttpPost]
    [RequirePermission(PermissionCodes.AppsCopy)]
    [ProducesResponseType(typeof(ApiResponse<CopyAppResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Copy([FromRoute] Guid appId, [FromBody] CopyAppRequest request, CancellationToken ct)
    {
        var result = await _copyHandler.HandleAsync(
            new CopyAppCommand(appId, request.Name, request.Description, request.CopyUsers, request.ProgressId,
                request.WithData, request.IncludeFiles), ct);

        var response = new CopyAppResponse
        {
            PublicId = result.PublicId,
            Name = result.Name,
            Description = result.Description,
            IsEncrypted = result.IsEncrypted,
            Warnings = result.Warnings.Select(w => new CopyAppWarningResponse { Code = w.Code, Message = w.Message }).ToList(),
            RecordsCopied = result.RecordsCopied
        };
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<CopyAppResponse>(response));
    }

    /// <summary>How far the caller's running copy is (the progressId it sent with the copy), for the dialog's progress bar.
    /// A copy with data also gives its result (or error) here once it ended. Data is null when there is no such copy for
    /// the caller: not started yet, ended long ago, or a copy without data run on another server instance. Polled while
    /// the copy runs.</summary>
    [HttpGet("progress/{progressId:guid}")]
    [RequirePermission(PermissionCodes.AppsCopy)]
    [ProducesResponseType(typeof(ApiResponse<AppCopyProgressResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Progress([FromRoute] Guid appId, [FromRoute] Guid progressId,
        [FromServices] GetAppCopyProgressQueryHandler handler, CancellationToken ct)
    {
        var progress = await handler.HandleAsync(progressId, ct);
        var response = progress is null ? null : new AppCopyProgressResponse
        {
            Percent = progress.Percent,
            Stage = progress.Stage,
            IsDone = progress.IsDone,
            IsFailed = progress.IsFailed,
            AppPublicId = progress.NewAppPublicId,
            RecordsCopied = progress.RecordsCopied,
            Warnings = progress.Warnings?.Select(w => new CopyAppWarningResponse { Code = w.Code, Message = w.Message }).ToList(),
            Error = progress.Error
        };
        return Ok(new ApiResponse<AppCopyProgressResponse?>(response));
    }
}
