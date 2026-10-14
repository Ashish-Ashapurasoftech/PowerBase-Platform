using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.Application.Imports;
using PowerBase.Application.Imports.Files;

namespace PowerBase.API.Controllers;

/// <summary>The imports of an app for a PowerFlow step: list them (choose the app, then a table), start one (a table import with just its id, a file import with
/// its file in the same call) and follow the run. Separate from the import screen's own endpoints, which it neither calls nor changes: the run is started
/// by the same code as the import run API, so it is checked, queued and reported the same way.</summary>
[ApiController]
[RequireAuth]
[Produces("application/json")]
[Route("apps/{appId:guid}/powerflow")]
public sealed class PowerFlowImportTemplatesController(
    ListPowerFlowImportTemplatesHandler list, StartPowerFlowImportHandler start, GetImportRunStatusHandler status, GetImportFeedbackHandler feedback,
    IConfiguration configuration) : ControllerBase
{
    private const long UploadLimit = ImportFileFormats.MaxFileBytes + 1024 * 1024; // the file, plus the form around it

    /// <summary>What a PowerFlow sends to start an import. A <b>table</b> import needs nothing here. A <b>file</b> import needs <c>file</c>.</summary>
    public sealed class RunImportForm
    {
        /// <summary>The file to import (a CSV or an Excel .xlsx file). Required when the import reads a file; must be left out when it reads a table.</summary>
        public IFormFile? File { get; set; }

        /// <summary>Optional. People to email when the run finishes, besides the token's own user: addresses separated by commas or semicolons. Replaces the people saved on the import for this run only.</summary>
        public string? NotifyEmails { get; set; }
    }

    /// <summary>Imports of the app, newest activity first. Every filter is optional and they are combined with AND:
    /// <paramref name="tableId"/>: only imports that fill this table (as their first table or any other; a table of another app is a 404).
    /// <paramref name="templateId"/>: only this import. <paramref name="sourceKind"/>: only imports that read a "file" or a "table" (any case; anything
    /// else is a 400). Neither: every import of the app. A malformed or empty id is a 400; a caller who is not a member of the app is refused.</summary>
    [HttpGet("import-templates")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)] // a person's own view of the app: never kept by a shared cache
    public async Task<IActionResult> List(
        Guid appId, [FromQuery] Guid? tableId, [FromQuery] Guid? templateId, [FromQuery] string? sourceKind, CancellationToken ct) =>
        Ok(new { data = await list.HandleAsync(appId, tableId, templateId, sourceKind, ct) });

    /// <summary>Starts the import and returns at once with the run's id (202); the import runs in the background. <paramref name="templateId"/> is the
    /// <c>templateId</c> of the list. <b>Table import:</b> send no file. <b>File import:</b> send the file as the form field <c>file</c> (multipart/form-data);
    /// it is read by this run and deleted when the run ends. Send an <c>X-PowerBase-Client-Token</c> header (any unique string, up to 200 characters) to make a
    /// retry safe: the same token for the same import returns the same run (200, <c>replayed: true</c>) and the file sent again is discarded.
    /// 400: no file for a file import, a file for a table import, a wrong or empty file, a bad email list; 403: no permission on the import's tables;
    /// 404: no such import in the app; 409: the import is already running.</summary>
    [HttpPost("import-templates/{templateId:guid}/run")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(UploadLimit)]
    [RequestFormLimits(MultipartBodyLengthLimit = UploadLimit)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Run(
        Guid appId, Guid templateId, [FromForm] RunImportForm form,
        [FromHeader(Name = "X-PowerBase-Client-Token")] string? clientToken, CancellationToken ct)
    {
        await using var content = form.File?.OpenReadStream();
        var started = await start.HandleAsync(
            appId, templateId, content, form.File?.FileName, form.File?.Length ?? 0, form.NotifyEmails, clientToken, configuration["Frontend:BaseUrl"], ct);
        var body = new { data = new { runId = started.RunId, replayed = started.Replayed } };
        return started.Replayed ? Ok(body) : Accepted(body);
    }

    /// <summary>Where a run is, and how it ended. Poll this until <c>status</c> is success, partial, failed or cancelled. Counts and status only, never rows.</summary>
    [HttpGet("import-runs/{runId:guid}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Status(Guid appId, Guid runId, CancellationToken ct) =>
        Ok(new { data = await status.HandleAsync(appId, runId, ct) });

    /// <summary>The CSV of every row that was not imported, with the reason. Only the person who started the run, or an admin.</summary>
    [HttpGet("import-runs/{runId:guid}/feedback")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Feedback(Guid appId, Guid runId, CancellationToken ct)
    {
        await status.HandleAsync(appId, runId, ct); // same app check as the status call
        var file = await feedback.HandleAsync(runId, ct);
        return File(file.Content, "text/csv", file.FileName);
    }
}
