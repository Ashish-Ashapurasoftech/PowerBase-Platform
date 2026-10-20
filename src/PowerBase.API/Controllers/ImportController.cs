using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.Application.Import.Jobs;
using PowerBase.Application.Imports;
using PowerBase.Application.Import.Commands.ImportAppFromPbl;
using PowerBase.Application.Import.Commands.UpdateAppFromPbl;
using PowerBase.Application.Import.Diff;
using PowerBase.Application.Import.Mapping;
using PowerBase.Application.Import.Queries.ImportDiff;
using PowerBase.Application.Import.Queries.ImportPreview;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.API.Controllers;

/// <summary>Import an app from a QBL (Quickbase) or PBL (PowerBase Blueprint Language) document —
/// the format is detected from the content. Create a new app, or update an existing one after
/// reviewing a diff.</summary>
[ApiController]
[Route("apps/import")]
[RequireAuth]
public class ImportController : ControllerBase
{
    // Real QBL exports run to several MB (a 95-table app is ~9 MB).
    private const long UploadLimit = 64L * 1024 * 1024;

    private readonly ImportPreviewQueryHandler _previewHandler;
    private readonly ImportAppFromPblCommandHandler _importHandler;
    private readonly ImportDiffQueryHandler _diffHandler;
    private readonly UpdateAppFromPblCommandHandler _updateHandler;
    private readonly AppImportJobService _jobs;
    private readonly IConfiguration _configuration;

    public ImportController(
        AppImportJobService jobs,
        IConfiguration configuration,
        ImportPreviewQueryHandler previewHandler,
        ImportAppFromPblCommandHandler importHandler,
        ImportDiffQueryHandler diffHandler,
        UpdateAppFromPblCommandHandler updateHandler)
    {
        _previewHandler = previewHandler;
        _importHandler = importHandler;
        _diffHandler = diffHandler;
        _updateHandler = updateHandler;
        _jobs = jobs;
        _configuration = configuration;
    }

    /// <summary>Parse and validate a QBL/PBL document, returning a preview of the tables/fields
    /// that would be created and any issues detected, without creating anything.</summary>
    [HttpPost("preview")]
    [RequirePermission(PermissionCodes.AppsCreate)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(UploadLimit)]
    [RequestFormLimits(MultipartBodyLengthLimit = UploadLimit)]
    [ProducesResponseType(typeof(ApiResponse<ImportPreviewResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Preview(IFormFile file, CancellationToken ct)
    {
        var content = await ReadFileAsync(file, ct);
        var result = await _previewHandler.HandleAsync(new ImportPreviewQuery(content), ct);
        return Ok(new ApiResponse<ImportPreviewResult>(result));
    }

    /// <summary>Import a QBL/PBL document as a brand-new app. Optional <c>mapping</c> (JSON) may
    /// list <c>excludedTableRefs</c> — tables the user chose not to import.</summary>
    [HttpPost]
    [RequirePermission(PermissionCodes.AppsCreate)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(UploadLimit)]
    [RequestFormLimits(MultipartBodyLengthLimit = UploadLimit)]
    [ProducesResponseType(typeof(ApiResponse<ImportReport>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<AppImportJobDto>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Import(
        IFormFile file,
        [FromForm] string? mapping,
        [FromQuery] ImportMode mode = ImportMode.CreateNewApp,
        CancellationToken ct = default)
    {
        // Big files take minutes: accept the upload now, import in the background and email the result.
        if (mode == ImportMode.CreateNewApp && file is not null && AppImportJobPolicy.RunsInBackground(file.Length))
        {
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ct);
            var job = await _jobs.StartAsync(file.FileName, buffer.ToArray(), mapping, FrontendBaseUrl(), ct);
            return Accepted(new ApiResponse<AppImportJobDto>(job));
        }

        var content = await ReadFileAsync(file, ct);
        var result = await _importHandler.HandleAsync(new ImportAppFromPblCommand(content, mode, ImportMapping.Parse(mapping)), ct);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<ImportReport>(result));
    }

    /// <summary>Dry run for updating an existing app: lists every element as New, Changed,
    /// Identical or Conflict, plus what exists only in the app. Writes nothing. Requires the
    /// Administrator role on the target app.</summary>
    [HttpPost("diff")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(UploadLimit)]
    [RequestFormLimits(MultipartBodyLengthLimit = UploadLimit)]
    [ProducesResponseType(typeof(ApiResponse<ImportDiffResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Diff(IFormFile file, [FromForm] Guid targetAppId, [FromForm] string? mapping, CancellationToken ct)
    {
        var content = await ReadFileAsync(file, ct);
        var result = await _diffHandler.HandleAsync(new ImportDiffQuery(content, targetAppId, ImportMapping.Parse(mapping)), ct);
        return Ok(new ApiResponse<ImportDiffResult>(result));
    }

    /// <summary>Apply the user's decisions to an existing app. Adds only approved new elements,
    /// overwrites only approved changed ones (labels, descriptions, variable values), and never
    /// deletes. A changed element without an explicit decision rejects the request (400
    /// <c>UNDECIDED_CHANGED_ELEMENTS</c>). Safe to re-run. Requires the Administrator role on the target app.</summary>
    [HttpPost("update")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(UploadLimit)]
    [RequestFormLimits(MultipartBodyLengthLimit = UploadLimit)]
    [ProducesResponseType(typeof(ApiResponse<ImportUpdateReport>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Update(IFormFile file, [FromForm] Guid targetAppId, [FromForm] string? mapping, CancellationToken ct)
    {
        var content = await ReadFileAsync(file, ct);
        var result = await _updateHandler.HandleAsync(new UpdateAppFromPblCommand(content, targetAppId, ImportMapping.Parse(mapping)), ct);
        return Ok(new ApiResponse<ImportUpdateReport>(result));
    }

    /// <summary>The caller's background imports (running first, then recent), for the app list.</summary>
    [HttpGet("jobs")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<AppImportJobDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListJobs(CancellationToken ct) =>
        Ok(new ApiResponse<IReadOnlyList<AppImportJobDto>>(await _jobs.ListMineAsync(ct)));

    [HttpGet("jobs/{jobId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<AppImportJobDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetJob(Guid jobId, CancellationToken ct) =>
        Ok(new ApiResponse<AppImportJobDto>(await _jobs.GetAsync(jobId, ct)));

    /// <summary>The full import report (skipped items, formula diagnostics) of a finished background import.</summary>
    [HttpGet("jobs/{jobId:guid}/report")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetJobReport(Guid jobId, CancellationToken ct) =>
        Ok(new { data = await _jobs.GetResultAsync(jobId, ct) });

    /// <summary>Hides a finished background import from the list.</summary>
    [HttpDelete("jobs/{jobId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DismissJob(Guid jobId, CancellationToken ct)
    {
        await _jobs.DismissAsync(jobId, ct);
        return NoContent();
    }

    /// <summary>The address the completion email links to: the configured frontend, or the caller's Origin when it is a known frontend.</summary>
    private string? FrontendBaseUrl() => ImportLinks.TrustedBaseUrl(
        _configuration["Frontend:BaseUrl"], Request.Headers.Origin.ToString(), _configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? []);

    private static async Task<string> ReadFileAsync(IFormFile? file, CancellationToken ct)
    {
        if (file == null || file.Length == 0)
            throw new BadRequestException("FILE_UPLOAD_ERROR", "No file was uploaded.");

        using var reader = new StreamReader(file.OpenReadStream());
        return await reader.ReadToEndAsync(ct);
    }
}
