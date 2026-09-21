using System.Text.Json;
using System.Text.RegularExpressions;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Pipelines;

public static class UploadFileStepContract
{
    public const long MaxFileSizeBytes = 71L * 1024 * 1024;
    public const int MaxFileNameLength = 225;

    public static string NormalizeFileSourceToken(string value)
    {
        return Regex.Replace(value,
            @"^\{\{\s*\(\s*steps\.([A-Za-z0-9_]+)(\.item)?\.([A-Za-z0-9_]+)\s*\|\s*from_json\s*\)\.path\s*\}\}$",
            match => $"{{{{steps.{match.Groups[1].Value}{match.Groups[2].Value}.{match.Groups[3].Value}}}}}");
    }

    public static string ResolveDownloadSource(string? value)
    {
        var candidate = ExtractUrl(value);
        if (candidate.StartsWith("/files/", StringComparison.OrdinalIgnoreCase)) return candidate;
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return uri.ToString();
        throw new InvalidOperationException("Upload a File requires a public HTTP(S) URL or File Transfer Handle.");
    }

    public static string NormalizeFileName(string? value)
    {
        var fileName = Path.GetFileName(value?.Trim());
        if (string.IsNullOrWhiteSpace(fileName))
            throw new InvalidOperationException("Upload a File requires a file name.");
        if (fileName.Length > MaxFileNameLength)
            throw new InvalidOperationException("Upload a File supports file names up to 225 characters.");
        return fileName;
    }

    public static string EnsureFileExtension(
        string fileName,
        Uri sourceUrl,
        string? responseFileName,
        string? contentType,
        Stream? content = null)
    {
        if (!string.IsNullOrWhiteSpace(Path.GetExtension(fileName))) return NormalizeFileName(fileName);

        var responseExtension = Path.GetExtension(responseFileName?.Trim().Trim('"'));
        if (IsSafeExtension(responseExtension)) return NormalizeFileName(fileName + responseExtension);

        var sourceExtension = Path.GetExtension(Uri.UnescapeDataString(sourceUrl.AbsolutePath));
        if (IsSafeExtension(sourceExtension)) return NormalizeFileName(fileName + sourceExtension);

        var mediaType = contentType?.Split(';', 2)[0].Trim().ToLowerInvariant();
        var contentExtension = mediaType switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/svg+xml" => ".svg",
            "application/pdf" => ".pdf",
            "text/plain" => ".txt",
            "text/csv" => ".csv",
            "application/json" => ".json",
            "application/zip" => ".zip",
            _ => string.Empty
        };
        if (!string.IsNullOrEmpty(contentExtension)) return NormalizeFileName(fileName + contentExtension);

        return NormalizeFileName(fileName + DetectFileExtension(content));
    }

    public static string PreferSourceFileName(
        string configuredFileName,
        Uri sourceUrl,
        string? responseFileName)
    {
        var headerName = responseFileName?.Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(headerName))
            return NormalizeFileName(headerName);

        var urlName = Path.GetFileName(Uri.UnescapeDataString(sourceUrl.AbsolutePath));
        // A route such as /download or /content is not a reliable file name. In that case the
        // configured fallback remains useful and EnsureFileExtension can infer its extension.
        if (!string.IsNullOrWhiteSpace(urlName) && !string.IsNullOrWhiteSpace(Path.GetExtension(urlName)))
            return NormalizeFileName(urlName);

        return NormalizeFileName(configuredFileName);
    }

    private static string DetectFileExtension(Stream? content)
    {
        if (content == null || !content.CanRead || !content.CanSeek) return string.Empty;
        var originalPosition = content.Position;
        Span<byte> header = stackalloc byte[12];
        var bytesRead = content.Read(header);
        content.Position = originalPosition;

        if (bytesRead >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) return ".jpg";
        if (bytesRead >= 8 && header[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return ".png";
        if (bytesRead >= 6 && (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8))) return ".gif";
        if (bytesRead >= 4 && header[..4].SequenceEqual("%PDF"u8)) return ".pdf";
        if (bytesRead >= 4 && header[..4].SequenceEqual(new byte[] { 0x50, 0x4B, 0x03, 0x04 })) return ".zip";
        if (bytesRead >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8)) return ".webp";
        return string.Empty;
    }

    private static bool IsSafeExtension(string? extension)
    {
        return !string.IsNullOrWhiteSpace(extension) && extension.Length <= 10 &&
               extension[0] == '.' && extension.Skip(1).All(char.IsLetterOrDigit);
    }

    public static string? ReadConfigValue(string? configJson, params string[] names)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return null;
        using var document = JsonDocument.Parse(configJson);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (names.Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase)) &&
                property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
        }
        return null;
    }

    public static Guid ResolveRecordPublicId(object? output)
    {
        if (output == null) return Guid.Empty;
        return FindRecordPublicId(JsonSerializer.SerializeToElement(output));
    }

    public static string SerializeAttachmentValue(StoredFile file)
    {
        return JsonSerializer.Serialize(new
        {
            name = file.Name,
            path = file.Path,
            size = file.Size,
            type = file.ContentType ?? string.Empty
        });
    }

    private static Guid FindRecordPublicId(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var found = FindRecordPublicId(item);
                if (found != Guid.Empty) return found;
            }
            return Guid.Empty;
        }
        if (element.ValueKind != JsonValueKind.Object) return Guid.Empty;
        string[] names = ["CreatedRecordPublicId", "UpdatedRecordPublicId", "RecordPublicId", "RecordId", "id"];
        foreach (var name in names)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    Guid.TryParse(property.Value.ToString(), out var recordPublicId))
                    return recordPublicId;
            }
        }
        foreach (var property in element.EnumerateObject())
        {
            var found = FindRecordPublicId(property.Value);
            if (found != Guid.Empty) return found;
        }
        return Guid.Empty;
    }

    public static async Task<MemoryStream> ReadWithLimitAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength > MaxFileSizeBytes)
            throw new InvalidOperationException("Upload a File supports files up to 71 MB.");

        await using var source = await content.ReadAsStreamAsync(ct);
        return await ReadWithLimitAsync(source, ct);
    }

    public static async Task<MemoryStream> ReadWithLimitAsync(Stream source, CancellationToken ct)
    {
        var result = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read == 0) break;
            total += read;
            if (total > MaxFileSizeBytes)
            {
                result.Dispose();
                throw new InvalidOperationException("Upload a File supports files up to 71 MB.");
            }
            await result.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        result.Position = 0;
        return result;
    }

    private static string ExtractUrl(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('[') && !trimmed.StartsWith('"'))
            return trimmed;

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            return FindUrl(document.RootElement) ?? trimmed;
        }
        catch (JsonException)
        {
            return trimmed;
        }
    }

    private static string? FindUrl(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String) return element.GetString();
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var found = FindUrl(item);
                if (!string.IsNullOrWhiteSpace(found)) return found;
            }
            return null;
        }
        if (element.ValueKind != JsonValueKind.Object) return null;

        string[] names = ["file_transfer_handle", "fileTransferHandle", "FileTransferHandle",
            "file_transfer_url", "fileTransferUrl", "FileTransferUrl", "browser_url", "BrowserUrl",
            "url", "Url", "path", "Path"];
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(property.GetString()))
                return property.GetString();
        }
        foreach (var property in element.EnumerateObject())
        {
            var found = FindUrl(property.Value);
            if (!string.IsNullOrWhiteSpace(found)) return found;
        }
        return null;
    }
}
