using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.Domain.Exceptions;
using System.IO;

namespace PowerBase.API.Controllers;

[ApiController]
[Route("apps/{appId:guid}/files")]
[RequireAuth]
public class FilesController : ControllerBase
{
    private readonly IConfiguration _configuration;

    public FilesController(IConfiguration configuration)
    {
        _configuration = configuration;
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

        var localPath = _configuration.GetValue<string>("Storage:LocalPath") ?? "C:\\PowerbaseUploads";
        var baseUrl = _configuration.GetValue<string>("Storage:BaseUrl") ?? "http://localhost:5000/files";

        // Ensure directory exists
        if (!Directory.Exists(localPath))
        {
            Directory.CreateDirectory(localPath);
        }

        // Generate a unique file name
        var fileExtension = Path.GetExtension(file.FileName);
        var uniqueFileName = $"{Guid.NewGuid()}{fileExtension}";
        var physicalPath = Path.Combine(localPath, uniqueFileName);

        // Save to disk
        using (var stream = new FileStream(physicalPath, FileMode.Create))
        {
            await file.CopyToAsync(stream, ct);
        }

        // Clean trailing slash of base URL
        baseUrl = baseUrl.TrimEnd('/');

        // Build file URL/path mapping (e.g. /files/filename.ext)
        var relativePath = $"/files/{uniqueFileName}";

        var response = new FileUploadResponse
        {
            Name = file.FileName,
            Path = relativePath,
            Size = file.Length,
            Type = file.ContentType
        };

        return Ok(new ApiResponse<FileUploadResponse>(response));
    }
}

public class FileUploadResponse
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Type { get; set; } = string.Empty;
}
