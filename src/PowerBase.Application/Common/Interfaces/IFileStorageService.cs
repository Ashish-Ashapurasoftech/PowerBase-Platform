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
    Task<StoredFile> SaveAsync(Stream content, string fileName, string? contentType, CancellationToken ct = default, string? uniqueKey = null);

    /// <summary>Deletes a previously stored file, identified by the relative path returned
    /// from <see cref="SaveAsync"/>. No-op if it does not exist.</summary>
    Task DeleteAsync(string relativePath, CancellationToken ct = default);
}

/// <summary>
/// Optional read capability for storage providers. Kept separate from
/// <see cref="IFileStorageService"/> so existing storage implementations and consumers retain
/// their original contract.
/// </summary>
public interface IFileStorageReadService
{
    Task<Stream> OpenReadAsync(string path, CancellationToken ct = default);
}

/// <summary>
/// Optional copy capability for storage providers (Copy App with data gives the copy its own files). Kept separate from
/// <see cref="IFileStorageService"/> like <see cref="IFileStorageReadService"/>.
/// </summary>
public interface IFileStorageCopyService
{
    /// <summary>Copies the stored file at <paramref name="sourcePath"/> (a path returned by
    /// <see cref="IFileStorageService.SaveAsync"/>) to a new file named <paramref name="newName"/>, and returns the new
    /// file's path in the same form. Throws <see cref="FileNotFoundException"/> when the source file does not exist.</summary>
    Task<string> CopyAsync(string sourcePath, string newName, CancellationToken ct = default);

    /// <summary>Deletes every stored file whose name starts with <paramref name="prefix"/>. Used to remove the files of a
    /// copy that failed.</summary>
    Task DeleteByPrefixAsync(string prefix, CancellationToken ct = default);
}

public sealed class StoredFile
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public long Size { get; init; }
    public string? ContentType { get; init; }
}
