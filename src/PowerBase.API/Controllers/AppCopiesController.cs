using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.API.Models.Apps;
using PowerBase.Application.Apps.Commands.DismissAppCopy;
using PowerBase.Application.Apps.Queries.ListMyAppCopies;
using PowerBase.Domain.Constants;

namespace PowerBase.API.Controllers;

/// <summary>The caller's copies with data (Copy App – Schema + Data) for the app list. Needs only apps:read: the app list
/// asks for them on every visit, also for users who cannot create apps (who then get an empty list, not a 403 toast).</summary>
[ApiController]
[Route("apps/copies")]
public class AppCopiesController : ControllerBase
{
    /// <summary>The caller's copies with data: running ones with their progress, and finished or failed ones of the last
    /// 7 days that were not dismissed. Newest first. Polled by the app list while a copy runs.</summary>
    [HttpGet]
    [RequirePermission(PermissionCodes.AppsRead)]
    [ProducesResponseType(typeof(ApiResponse<List<AppCopyRunResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List([FromServices] ListMyAppCopiesQueryHandler handler, CancellationToken ct)
    {
        var runs = await handler.HandleAsync(ct);
        var response = runs.Select(r => new AppCopyRunResponse
        {
            Id = r.Id,
            Name = r.Name,
            SourceAppPublicId = r.SourceAppPublicId,
            SourceAppName = r.SourceAppName,
            Status = r.Status,
            Percent = r.Percent,
            Stage = r.Stage,
            RecordsCopied = r.RecordsCopied,
            TotalRecords = r.TotalRecords,
            IncludeFiles = r.IncludeFiles,
            ElapsedSeconds = r.ElapsedSeconds,
            Error = r.Error
        }).ToList();
        return Ok(new ApiResponse<List<AppCopyRunResponse>>(response));
    }

    /// <summary>Hides a finished or failed copy from the app list; <paramref name="id"/> is the copy's app PublicId. A running
    /// copy stays listed.</summary>
    [HttpDelete("{id:guid}")]
    [RequirePermission(PermissionCodes.AppsRead)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Dismiss([FromRoute] Guid id, [FromServices] DismissAppCopyCommandHandler handler, CancellationToken ct)
    {
        await handler.HandleAsync(id, ct);
        return NoContent();
    }
}
