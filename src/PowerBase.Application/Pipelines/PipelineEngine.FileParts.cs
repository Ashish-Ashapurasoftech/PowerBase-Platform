using System.Text.Json;
using System.Text.RegularExpressions;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Pipelines;

/// <summary>
/// The parts of a File attachment field as steps read them, the way Quickbase offers them under a file field: File transfer handle,
/// Browser url, File name and File size — <c>{{steps.ref.fid_N.file_transfer_handle}}</c> and so on. The field stores the attachment
/// as JSON (<c>[{"name","path","size","contentType"}]</c>, the current file first, or one object as Upload a File writes it).
/// Only <c>fid_N.part</c> references with these four names are handled here; every other token goes the way it always has.
/// </summary>
public partial class PipelineEngine
{
    private static readonly Regex FileSubTokenRegex = new(
        @"\{\{\s*([^{}]*?\.fid_\d+)\.(file_transfer_handle|browser_url|file_name|file_size)\s*\}\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Swaps each <c>{{…fid_N.file_name}}</c>-style token for that part of the file the field holds: empty when the field
    /// has no file. A token whose field cannot be read, or whose value is not a file, is left as written.</summary>
    private string ExpandFileSubTokens(string input, string payloadJson, string? executionPath, List<PipelineStep>? allSteps)
        => FileSubTokenRegex.Replace(input, match =>
        {
            string raw;
            try { raw = EvaluateTokens("{{" + match.Groups[1].Value + "}}", payloadJson, executionPath, allSteps); }
            catch (Exception) { return match.Value; }
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            var file = PipelineFileFieldParts.Read(raw);
            if (file is null) return match.Value;
            var part = match.Groups[2].Value.ToLowerInvariant();
            if (part != "browser_url") return PipelineFileFieldParts.Part(file, part, null, null);
            return FileBrowserUrl(file, allSteps);
        });

    /// <summary>The address a browser opens the file at, as Quickbase's Browser url: a full link on the address the tenant's
    /// users reach the API at (no configuration), relative only until this API has seen a signed-in request for the tenant.</summary>
    private string FileBrowserUrl(PipelineFileFieldParts.FileValue file, List<PipelineStep>? allSteps)
    {
        var apiBase = _serviceProvider.GetService(typeof(IRequestOriginRegistry)) is IRequestOriginRegistry origins
            ? origins.Get(_queryContext.TenantId) : null;
        return PipelineFileFieldParts.Part(file, "browser_url", apiBase, FormulaAppId(allSteps, _serviceProvider));
    }

    /// <summary>What Upload a File exports for later steps, as Quickbase's step does: the four parts of the file it stored.</summary>
    private Dictionary<string, object?> UploadedFileExports(string name, string path, long? size, List<PipelineStep>? allSteps)
    {
        var file = new PipelineFileFieldParts.FileValue(name, path, size);
        return new Dictionary<string, object?>
        {
            ["file_transfer_handle"] = path,
            ["browser_url"] = FileBrowserUrl(file, allSteps),
            ["file_name"] = name,
            ["file_size"] = size
        };
    }

    private static bool IsFileSubToken(string token) =>
        FileSubTokenRegex.Match(token) is { Success: true } match && match.Length == token.Length;
}

/// <summary>Reads the parts of a stored File attachment value.</summary>
public static class PipelineFileFieldParts
{
    public sealed record FileValue(string Name, string Path, long? Size);

    /// <summary>The current file of a File field value: the first entry of the stored array, or the object itself. Null when the
    /// value is not a file attachment.</summary>
    public static FileValue? Read(string raw)
    {
        var text = raw.Trim();
        if (!(text.StartsWith('[') || text.StartsWith('{'))) return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var element = doc.RootElement;
            if (element.ValueKind == JsonValueKind.Array)
            {
                if (element.GetArrayLength() == 0) return new FileValue(string.Empty, string.Empty, null);
                element = element[0];
            }
            if (element.ValueKind != JsonValueKind.Object) return null;
            string Text(string name) => element.EnumerateObject()
                .FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Value.ValueKind: JsonValueKind.String } p
                ? p.Value.GetString() ?? string.Empty : string.Empty;
            var path = Text("path");
            if (string.IsNullOrEmpty(path)) return null;
            long? size = null;
            var sizeProp = element.EnumerateObject().FirstOrDefault(p => p.Name.Equals("size", StringComparison.OrdinalIgnoreCase));
            if (sizeProp.Value.ValueKind == JsonValueKind.Number && sizeProp.Value.TryGetInt64(out var s)) size = s;
            return new FileValue(Text("name"), path, size);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>One part of the file. The transfer handle is the stored path, which Upload a File reads straight from storage. The
    /// browser url is the app's download address for the file (absolute when the API address is known).</summary>
    public static string Part(FileValue file, string part, string? apiBaseUrl, string? appPublicId)
    {
        if (string.IsNullOrEmpty(file.Path)) return string.Empty;
        return part switch
        {
            "file_transfer_handle" => file.Path,
            "file_name" => file.Name,
            "file_size" => file.Size?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            "browser_url" => BrowserUrl(file, apiBaseUrl, appPublicId),
            _ => string.Empty
        };
    }

    private static string BrowserUrl(FileValue file, string? apiBaseUrl, string? appPublicId)
    {
        if (string.IsNullOrWhiteSpace(appPublicId)) return file.Path;
        var name = string.IsNullOrWhiteSpace(file.Name) ? System.IO.Path.GetFileName(file.Path) : file.Name;
        var relative = $"/apps/{appPublicId}/files/download?path={Uri.EscapeDataString(file.Path)}&fileName={Uri.EscapeDataString(name)}";
        return string.IsNullOrWhiteSpace(apiBaseUrl) ? relative : apiBaseUrl.TrimEnd('/') + relative;
    }

    private static readonly Regex DownloadUrlRegex = new(
        @"^(?:https?://[^/]+)?/apps/[0-9a-fA-F-]{36}/files/download\?(?:[^#]*&)?path=([^&#]+)", RegexOptions.Compiled);

    /// <summary>The stored path behind a browser url this app gave out (<c>…/apps/{app}/files/download?path=/files/…</c>), so Upload a
    /// File reads it from storage as Quickbase reads its own file links. Null for any other address.</summary>
    public static string? StoredPathOfDownloadUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var match = DownloadUrlRegex.Match(url.Trim());
        if (!match.Success) return null;
        var path = Uri.UnescapeDataString(match.Groups[1].Value);
        return path.StartsWith("/files/", StringComparison.OrdinalIgnoreCase) ? path : null;
    }
}
