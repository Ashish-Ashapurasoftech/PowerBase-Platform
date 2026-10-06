using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.Application.Imports;

namespace PowerBase.API.Controllers;

/// <summary>The import run API: start a saved import and follow it from a script, a scheduler or another system. It works with
/// the same bearer tokens as the rest of the API, including user tokens limited to chosen apps, and applies the same checks as
/// the Run button, as the token's user. Responses carry counts and status only, never rows.</summary>
[ApiController]
[RequireAuth]
[Route("apps/{appId:guid}/imports")]
public sealed class ImportRunApiController(
    StartImportRunHandler start, GetImportRunStatusHandler status, GetImportFeedbackHandler feedback, IConfiguration configuration) : ControllerBase
{
    public sealed record RunImportRequest(List<string>? NotifyEmails, Guid? FileId = null);

    /// <summary>Starts the import and returns at once with the run's id (202). Send an <c>X-PowerBase-Client-Token</c> header
    /// (any unique string, up to 200 characters) to make a retry safe: repeating the call with the same token returns the same
    /// run (200, <c>replayed: true</c>) instead of starting another.</summary>
    [HttpPost("{importId:guid}/run")]
    public async Task<IActionResult> Run(Guid appId, Guid importId, [FromBody] RunImportRequest? request, CancellationToken ct)
    {
        var clientToken = Request.Headers["X-PowerBase-Client-Token"].FirstOrDefault();
        var started = await start.StartAsync(importId, ImportTrigger.Api, request?.NotifyEmails, configuration["Frontend:BaseUrl"], appId, clientToken, request?.FileId, ct);
        var body = new { data = new { runId = started.RunId, replayed = started.Replayed } };
        return started.Replayed ? Ok(body) : Accepted(body);
    }

    /// <summary>Where a run is, and how it ended. Poll this until <c>status</c> is success, partial, failed or cancelled.</summary>
    [HttpGet("runs/{runId:guid}")]
    public async Task<IActionResult> Status(Guid appId, Guid runId, CancellationToken ct) =>
        Ok(new { data = await status.HandleAsync(appId, runId, ct) });

    /// <summary>The CSV of every row that was not imported, with the reason. Only the person who started the run, or an admin.</summary>
    [HttpGet("runs/{runId:guid}/feedback")]
    public async Task<IActionResult> Feedback(Guid appId, Guid runId, CancellationToken ct)
    {
        await status.HandleAsync(appId, runId, ct); // same app check as the status call
        var file = await feedback.HandleAsync(runId, ct);
        return File(file.Content, "text/csv", file.FileName);
    }
}
