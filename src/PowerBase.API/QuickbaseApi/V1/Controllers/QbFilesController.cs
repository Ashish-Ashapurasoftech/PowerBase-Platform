using Microsoft.AspNetCore.Mvc;
using PowerBase.API.QuickbaseApi.Common;
using PowerBase.API.QuickbaseApi.V1.Services;

namespace PowerBase.API.QuickbaseApi.V1.Controllers;

/// <summary>Quickbase-compatible "Files" API, version 1.</summary>
[Route(QbV1.ROUTE_PREFIX + "/files")]
[QbRestExceptionFilter, QbRequireAuth, QbRealmHostname, QbSortedJson]
public sealed class QbFilesController : ControllerBase
{
    private readonly QbFileService _files;
    public QbFilesController(QbFileService files) => _files = files;

    /// <summary>Download file (Quickbase downloadFile): GET /v1/files/{tableId}/{recordId}/{fieldId}/{versionNumber}. The body is
    /// the file content, base64-encoded, as in Quickbase. versionNumber 0 = the current file.</summary>
    [HttpGet("{tableId}/{recordId}/{fieldId}/{versionNumber:int}")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(QbErrorBody), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(QbErrorBody), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(QbErrorBody), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(QbErrorBody), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(string tableId, string recordId, string fieldId, int versionNumber, CancellationToken ct)
    {
        var (base64, fileName) = await _files.DownloadAsync(tableId, recordId, fieldId, versionNumber, ct);
        Response.Headers.ContentDisposition = new System.Net.Mime.ContentDisposition { FileName = fileName, Inline = false }.ToString();
        return Content(base64, "application/octet-stream");
    }

    /// <summary>Delete file (Quickbase deleteFile): DELETE /v1/files/{tableId}/{recordId}/{fieldId}/{versionNumber}. Answers the
    /// deleted version's details. versionNumber 0 = the current file.</summary>
    [HttpDelete("{tableId}/{recordId}/{fieldId}/{versionNumber:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(QbErrorBody), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(QbErrorBody), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(QbErrorBody), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(QbErrorBody), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(string tableId, string recordId, string fieldId, int versionNumber, CancellationToken ct)
        => Ok(await _files.DeleteAsync(tableId, recordId, fieldId, versionNumber, ct));
}
