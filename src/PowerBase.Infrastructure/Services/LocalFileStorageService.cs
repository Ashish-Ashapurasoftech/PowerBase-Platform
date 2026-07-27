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

    public async Task<StoredFile> SaveAsync(Stream content, string fileName, string? contentType, CancellationToken ct = default)
    {
        if (!Directory.Exists(_localPath))
            Directory.CreateDirectory(_localPath);

        var extension = Path.GetExtension(fileName);
        var uniqueFileName = $"{Guid.NewGuid()}{extension}";
        var physicalPath = Path.Combine(_localPath, uniqueFileName);

        await using (var stream = new FileStream(physicalPath, FileMode.Create))
        {
            await content.CopyToAsync(stream, ct);
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
