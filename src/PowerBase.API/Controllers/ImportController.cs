using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.Application.Import.Commands.ImportAppFromPbl;
using PowerBase.Application.Import.Queries.ImportPreview;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.API.Controllers;

/// <summary>Import an app from a PBL (PowerBase Blueprint Language) document. QBL (Quickbase)
/// files are converted to PBL ahead of this endpoint in a later phase; for now the uploaded
/// file must already be PBL JSON.</summary>
[ApiController]
[Route("apps/import")]
[RequireAuth]
public class ImportController : ControllerBase
{
    private readonly ImportPreviewQueryHandler _previewHandler;
    private readonly ImportAppFromPblCommandHandler _importHandler;

    public ImportController(ImportPreviewQueryHandler previewHandler, ImportAppFromPblCommandHandler importHandler)
    {
        _previewHandler = previewHandler;
        _importHandler = importHandler;
    }

    /// <summary>Parse and validate a PBL document, returning a preview of the tables/fields
    /// that would be created and any issues detected, without creating anything.</summary>
    [HttpPost("preview")]
    [RequirePermission(PermissionCodes.AppsCreate)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(ApiResponse<ImportPreviewResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Preview(IFormFile file, CancellationToken ct)
    {
        var json = await ReadFileAsync(file, ct);
        var result = await _previewHandler.HandleAsync(new ImportPreviewQuery(json), ct);
        return Ok(new ApiResponse<ImportPreviewResult>(result));
    }

    /// <summary>Import a PBL document. Phase 1 only supports creating a brand-new app
    /// (<see cref="ImportMode.CreateNewApp"/>).</summary>
    [HttpPost]
    [RequirePermission(PermissionCodes.AppsCreate)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(ApiResponse<ImportReport>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Import(IFormFile file, [FromQuery] ImportMode mode = ImportMode.CreateNewApp, CancellationToken ct = default)
    {
        var json = await ReadFileAsync(file, ct);
        var result = await _importHandler.HandleAsync(new ImportAppFromPblCommand(json, mode), ct);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<ImportReport>(result));
    }

    private static async Task<string> ReadFileAsync(IFormFile? file, CancellationToken ct)
    {
        if (file == null || file.Length == 0)
            throw new BadRequestException("FILE_UPLOAD_ERROR", "No file was uploaded.");

        using var reader = new StreamReader(file.OpenReadStream());
        return await reader.ReadToEndAsync(ct);
    }
}
