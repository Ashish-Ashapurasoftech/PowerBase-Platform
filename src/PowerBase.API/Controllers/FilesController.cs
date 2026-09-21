using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.API.Controllers;

[ApiController]
[Route("apps/{appId:guid}/files")]
[RequireAuth]
public class FilesController : ControllerBase
{
    private readonly IFileStorageService _storage;

    public FilesController(IFileStorageService storage)
    {
        _storage = storage;
    }

    /// <summary>Upload a file for the app context.</summary>
    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(ApiResponse<FileUploadResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Upload(Guid appId, IFormFile file, CancellationToken ct)
    {
        if (file == null || file.Length == 0)
        {
            throw new BadRequestException("FILE_UPLOAD_ERROR", "No file was uploaded.");
        }

        await using var stream = file.OpenReadStream();
        var stored = await _storage.SaveAsync(stream, file.FileName, file.ContentType, ct);

        var response = new FileUploadResponse
        {
            Name = stored.Name,
            Path = stored.Path,
            Size = stored.Size,
            Type = stored.ContentType ?? string.Empty,
            UploadedOn = DateTime.UtcNow,
            UploadedBy = HttpContext?.RequestServices.GetService<IQueryContext>()?.UserName ?? string.Empty,
        };

        return Ok(new ApiResponse<FileUploadResponse>(response));
    }

    /// <summary>
    /// Streams an attachment with its user-facing name. Storage providers intentionally use a
    /// unique physical/blob name, which must never leak into the browser download name.
    /// </summary>
    [HttpGet("download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(
        Guid appId,
        [FromQuery] string path,
        [FromQuery] string fileName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new BadRequestException("FILE_DOWNLOAD_ERROR", "A file path is required.");

        var safeFileName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeFileName))
            throw new BadRequestException("FILE_DOWNLOAD_ERROR", "A file name is required.");

        if (_storage is not IFileStorageReadService readableStorage)
            throw new InvalidOperationException("The configured file storage provider cannot download files.");

        var stream = await readableStorage.OpenReadAsync(path, ct);
        return File(stream, "application/octet-stream", safeFileName, enableRangeProcessing: true);
    }
}

public class FileUploadResponse
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Type { get; set; } = string.Empty;
    public DateTime UploadedOn { get; set; }
    public string UploadedBy { get; set; } = string.Empty;
}
