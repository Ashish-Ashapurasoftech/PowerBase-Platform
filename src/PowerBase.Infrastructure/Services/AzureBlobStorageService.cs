using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Infrastructure.Services;

/// <summary>
/// Azure Blob Storage implementation of <see cref="IFileStorageService"/>.
/// Automatically creates and manages the blob container and stores uploaded files securely in Azure Cloud.
/// </summary>
public sealed class AzureBlobStorageService : IFileStorageService, IFileStorageReadService
{
    private readonly BlobContainerClient? _containerClient;
    private readonly bool _isEnabled;

    public AzureBlobStorageService(IConfiguration configuration)
    {
        var connectionString = configuration["Storage:AzureBlob:ConnectionString"] 
                               ?? configuration["AzureBlob:ConnectionString"] 
                               ?? string.Empty;

        var containerName = configuration["Storage:AzureBlob:ContainerName"] 
                            ?? configuration["AzureBlob:ContainerName"] 
                            ?? "powerbase-uploads";

        if (!string.IsNullOrWhiteSpace(connectionString) && 
            !connectionString.StartsWith("<") && 
            (connectionString.StartsWith("DefaultEndpointsProtocol=", StringComparison.OrdinalIgnoreCase) || connectionString.Contains("AccountName=")))
        {
            _containerClient = new BlobContainerClient(connectionString, containerName);
            _isEnabled = true;
        }
        else
        {
            _containerClient = null;
            _isEnabled = false;
        }
    }

    private async Task EnsureContainerCreatedAsync(CancellationToken ct)
    {
        if (_containerClient != null)
        {
            await _containerClient.CreateIfNotExistsAsync(PublicAccessType.Blob, cancellationToken: ct);
        }
    }

    public async Task<StoredFile> SaveAsync(Stream content, string fileName, string? contentType, CancellationToken ct = default, string? uniqueKey = null)
    {
        if (!_isEnabled || _containerClient == null)
        {
            throw new InvalidOperationException("Azure Blob Storage is not configured. Please specify 'Storage:AzureBlob:ConnectionString' in appsettings.");
        }

        await EnsureContainerCreatedAsync(ct);

        var extension = Path.GetExtension(fileName);
        string uniqueBlobName = !string.IsNullOrWhiteSpace(uniqueKey)
            ? $"{uniqueKey}{extension}"
            : $"{Guid.NewGuid()}{extension}";

        var blobClient = _containerClient.GetBlobClient(uniqueBlobName);

        // Blob names are intentionally unique for collision safety. Preserve the friendly name
        // in response headers too, so a direct blob download never exposes the storage key.
        var downloadName = Path.GetFileName(fileName);
        var options = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders
            {
                ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
                ContentDisposition = $"attachment; filename*=UTF-8''{Uri.EscapeDataString(downloadName)}"
            }
        };

        // Upload stream to Azure Blob Storage
        await blobClient.UploadAsync(content, options, ct);

        long size = content.CanSeek ? content.Length : 0;

        return new StoredFile
        {
            Name = fileName,
            Path = blobClient.Uri.ToString(),
            Size = size,
            ContentType = contentType
        };
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
    {
        if (!_isEnabled || _containerClient == null)
            throw new InvalidOperationException("Azure Blob Storage is not configured.");

        var blobName = Path.GetFileName(Uri.TryCreate(path, UriKind.Absolute, out var uri) ? uri.LocalPath : path);
        var download = await _containerClient.GetBlobClient(blobName).DownloadStreamingAsync(cancellationToken: ct);
        return download.Value.Content;
    }

    public async Task DeleteAsync(string relativePath, CancellationToken ct = default)
    {
        if (!_isEnabled || _containerClient == null || string.IsNullOrWhiteSpace(relativePath))
            return;

        try
        {
            string blobName;
            if (Uri.TryCreate(relativePath, UriKind.Absolute, out var uri))
            {
                blobName = Path.GetFileName(uri.LocalPath);
            }
            else
            {
                blobName = Path.GetFileName(relativePath);
            }

            var blobClient = _containerClient.GetBlobClient(blobName);
            await blobClient.DeleteIfExistsAsync(cancellationToken: ct);
        }
        catch
        {
            // Best effort delete
        }
    }
}
