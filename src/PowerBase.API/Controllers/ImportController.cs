using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
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

    public ImportController(
        ImportPreviewQueryHandler previewHandler,
        ImportAppFromPblCommandHandler importHandler,
        ImportDiffQueryHandler diffHandler,
        UpdateAppFromPblCommandHandler updateHandler)
    {
        _previewHandler = previewHandler;
        _importHandler = importHandler;
        _diffHandler = diffHandler;
        _updateHandler = updateHandler;
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
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Import(
        IFormFile file,
        [FromForm] string? mapping,
        [FromQuery] ImportMode mode = ImportMode.CreateNewApp,
        CancellationToken ct = default)
    {
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

    private static async Task<string> ReadFileAsync(IFormFile? file, CancellationToken ct)
    {
        if (file == null || file.Length == 0)
            throw new BadRequestException("FILE_UPLOAD_ERROR", "No file was uploaded.");

        using var reader = new StreamReader(file.OpenReadStream());
        return await reader.ReadToEndAsync(ct);
    }
}
