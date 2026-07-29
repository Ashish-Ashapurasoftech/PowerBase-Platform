namespace PowerBase.Application.Common.Interfaces;

/// <summary>
/// Stores uploaded/captured binaries (file uploads, drawn signatures) and returns a
/// reference (relative path/URL) that can be persisted in a field value. The default
/// implementation writes to local disk (see <c>Storage:LocalPath</c>/<c>Storage:BaseUrl</c>
/// config); a future Azure Blob implementation can be swapped in without touching callers.
/// </summary>
public interface IFileStorageService
{
    /// <summary>Saves a stream under a caller-supplied file name and returns the stored
    /// reference (name, relative path, size, content type).</summary>
    Task<StoredFile> SaveAsync(Stream content, string fileName, string? contentType, CancellationToken ct = default);

    /// <summary>Deletes a previously stored file, identified by the relative path returned
    /// from <see cref="SaveAsync"/>. No-op if it does not exist.</summary>
    Task DeleteAsync(string relativePath, CancellationToken ct = default);
}

public sealed class StoredFile
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public long Size { get; init; }
    public string? ContentType { get; init; }
}
