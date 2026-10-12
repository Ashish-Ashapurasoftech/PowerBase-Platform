using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.Application.Imports.Files;

namespace PowerBase.API.Controllers;

/// <summary>Files to import from. A file is uploaded once, looked at (sheets, columns, first rows) while the import is set up, and
/// then read by a run; it is deleted when the run ends, or after a day. Only the person who uploaded a file can see or use it.</summary>
[ApiController]
[RequireAuth]
public sealed class ImportFilesController(
    UploadImportFileHandler upload, PreviewImportFileHandler preview, DiscardImportFileHandler discard, ColumnFitHandler columnFit,
    PreviewImportSheetsHandler sheetsPreview) : ControllerBase
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
        Guid fileId, [FromQuery] string? sheet, [FromQuery] int? headerRow, [FromQuery] int? dataStartRow, [FromQuery] string? delimiter, [FromQuery] bool allSheets, [FromQuery] int? rows, CancellationToken ct) =>
        Ok(new { data = await preview.HandleAsync(fileId, sheet, headerRow, dataStartRow, delimiter, ct, allSheets, rows) });

    /// <summary>Throws an uploaded file away (when the person changes their mind before running the import).</summary>
    [HttpDelete("import-files/{fileId:guid}")]
    public async Task<IActionResult> Discard(Guid fileId, CancellationToken ct)
    {
        await discard.HandleAsync(fileId, ct);
        return NoContent();
    }

    /// <summary>For a destination table: which columns of each sheet it is offered (the heading names one of its fields and the data fits that
    /// field's type). One call covers every sheet.</summary>
    [HttpPost("import-files/{fileId:guid}/column-fit")]
    public async Task<IActionResult> ColumnFit(Guid fileId, [FromBody] ImportColumnFitRequest request, CancellationToken ct) =>
        Ok(new { data = await columnFit.HandleAsync(fileId, request, ct) });

    /// <summary>The columns and row count of each sheet of an uploaded workbook, read with the layout given for each: one call for all.</summary>
    [HttpPost("import-files/{fileId:guid}/sheets")]
    public async Task<IActionResult> Sheets(Guid fileId, [FromBody] List<ImportSheetRequest>? request, CancellationToken ct) =>
        Ok(new { data = await sheetsPreview.HandleAsync(fileId, request, ct) });
}
