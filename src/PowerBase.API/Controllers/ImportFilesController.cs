using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.Application.Imports.Files;

namespace PowerBase.API.Controllers;

/// <summary>Files to import from. A file is uploaded once, looked at (sheets, columns, first rows) while the import is set up, and
/// then read by a run; it is deleted when the run ends, or after a day. Only the person who uploaded a file can see or use it.</summary>
[ApiController]
[RequireAuth]
public sealed class ImportFilesController(UploadImportFileHandler upload, PreviewImportFileHandler preview, DiscardImportFileHandler discard) : ControllerBase
{
    private const long Limit = ImportFileFormats.MaxFileBytes + 1024 * 1024; // the file, plus the form around it

    /// <summary>Uploads a CSV or Excel file for an import into this table. Returns its id, type and (for a workbook) its sheets.</summary>
    [HttpPost("tables/{tableId:guid}/import-files")]
    [RequestSizeLimit(Limit)]
    [RequestFormLimits(MultipartBodyLengthLimit = Limit)]
    public async Task<IActionResult> Upload(Guid tableId, IFormFile file, CancellationToken ct)
    {
        await using var content = file.OpenReadStream();
        return Ok(new { data = await upload.HandleAsync(tableId, content, file.FileName, file.Length, ct) });
    }

    /// <summary>The columns and first rows of the file, read as chosen. Changing the sheet, header row or separator asks again.</summary>
    [HttpGet("import-files/{fileId:guid}/preview")]
    public async Task<IActionResult> Preview(
        Guid fileId, [FromQuery] string? sheet, [FromQuery] int? headerRow, [FromQuery] int? dataStartRow, [FromQuery] string? delimiter, [FromQuery] bool allSheets, CancellationToken ct) =>
        Ok(new { data = await preview.HandleAsync(fileId, sheet, headerRow, dataStartRow, delimiter, ct, allSheets) });

    /// <summary>Throws an uploaded file away (when the person changes their mind before running the import).</summary>
    [HttpDelete("import-files/{fileId:guid}")]
    public async Task<IActionResult> Discard(Guid fileId, CancellationToken ct)
    {
        await discard.HandleAsync(fileId, ct);
        return NoContent();
    }
}
