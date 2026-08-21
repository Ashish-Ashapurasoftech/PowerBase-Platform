using Microsoft.Extensions.Configuration;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Infrastructure.Services;

/// <summary>Local-disk implementation of <see cref="IFileStorageService"/>, serving files
/// back via the app's static file middleware at <c>/files</c> (see Program.cs).</summary>
public sealed class LocalFileStorageService : IFileStorageService
{
    private readonly string _localPath;

    public LocalFileStorageService(IConfiguration configuration)
    {
        _localPath = configuration["Storage:LocalPath"] ?? "C:\\PowerbaseUploads";
    }

    public async Task<StoredFile> SaveAsync(Stream content, string fileName, string? contentType, CancellationToken ct = default, string? uniqueKey = null)
    {
        if (!Directory.Exists(_localPath))
            Directory.CreateDirectory(_localPath);

        var extension = Path.GetExtension(fileName);
        string uniqueFileName;
        string physicalPath;

        if (!string.IsNullOrEmpty(uniqueKey))
        {
            uniqueFileName = $"{uniqueKey}{extension}";
            physicalPath = Path.Combine(_localPath, uniqueFileName);

            if (File.Exists(physicalPath))
            {
                return new StoredFile
                {
                    Name = fileName,
                    Path = $"/files/{uniqueFileName}",
                    Size = new FileInfo(physicalPath).Length,
                    ContentType = contentType,
                };
            }

            var tempFileName = $"{uniqueKey}.{Guid.NewGuid()}.tmp";
            var tempPath = Path.Combine(_localPath, tempFileName);

            try
            {
                await using (var stream = new FileStream(tempPath, FileMode.Create))
                {
                    await content.CopyToAsync(stream, ct);
                }

                try
                {
                    File.Move(tempPath, physicalPath, overwrite: false);
                }
                catch (System.IO.IOException) when (File.Exists(physicalPath))
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch
                    {
                        // best-effort
                    }
                }
            }
            catch
            {
                try
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
                catch
                {
                    // best-effort
                }
                throw;
            }
        }
        else
        {
            uniqueFileName = $"{Guid.NewGuid()}{extension}";
            physicalPath = Path.Combine(_localPath, uniqueFileName);

            await using (var stream = new FileStream(physicalPath, FileMode.Create))
            {
                await content.CopyToAsync(stream, ct);
            }
        }

        return new StoredFile
        {
            Name = fileName,
            Path = $"/files/{uniqueFileName}",
            Size = new FileInfo(physicalPath).Length,
            ContentType = contentType,
        };
    }

    public Task DeleteAsync(string relativePath, CancellationToken ct = default)
    {
        var fileName = Path.GetFileName(relativePath);
        var physicalPath = Path.Combine(_localPath, fileName);
        if (File.Exists(physicalPath))
            File.Delete(physicalPath);
        return Task.CompletedTask;
    }
}
