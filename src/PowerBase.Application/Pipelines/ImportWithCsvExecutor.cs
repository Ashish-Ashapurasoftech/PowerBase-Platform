using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Application.Imports.Files;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pipelines;

/// <summary>The file an Import with CSV step hands to the import: a temporary copy, deleted when disposed.</summary>
public sealed class ImportCsvFile : IAsyncDisposable
{
    public required Stream Content { get; init; }
    public required string FileName { get; init; }
    public long Length => Content.Length;
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>The import run an Import with CSV step started, kept so a resumed or retried run follows that same import.</summary>
public sealed record ImportWithCsvRunReceipt(Guid ImportRunId, string? FileName, long FileSize);

/// <summary>
/// What the Import with CSV step does, apart from the engine's bookkeeping: fetch the file (an upload made in the step, a
/// stored file from an earlier step, or a URL), start the saved import with it through the same code as the import run API
/// (<see cref="StartPowerFlowImportHandler"/> — the same checks, permissions, queue and email), follow the run
/// (<see cref="GetImportRunStatusHandler"/>) and describe how it ended for later steps.
/// </summary>
public sealed class ImportWithCsvExecutor
{
    /// <summary>Distinguishes the receipt holding the started run from the step's own completion receipt.</summary>
    public const string RunReceiptSuffix = "#import-run";
    /// <summary>How often a running import is checked while the step waits inside the job.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    /// <summary>When the job is released to wait, how soon it comes back to check the import again.</summary>
    public static readonly TimeSpan ResumeInterval = TimeSpan.FromSeconds(30);

    private static readonly string[] FinishedStatuses = [ImportRunStatus.Success, ImportRunStatus.Partial, ImportRunStatus.Failed, ImportRunStatus.Cancelled];

    private readonly IServiceProvider _services;
    private readonly IFileStorageService? _fallbackStorage;
    private readonly Func<Uri, CancellationToken, Task<HttpResponseMessage>> _download;

    /// <param name="services">The step's services: the PowerFlow owner's account, or the connected account the step runs in.</param>
    /// <param name="fallbackStorage">The PowerFlow owner's file storage, for a file uploaded in the step while it runs in another account.</param>
    /// <param name="download">Fetches a URL (headers first; the body is streamed).</param>
    public ImportWithCsvExecutor(IServiceProvider services, IFileStorageService? fallbackStorage, Func<Uri, CancellationToken, Task<HttpResponseMessage>> download)
    {
        _services = services;
        _fallbackStorage = fallbackStorage;
        _download = download;
    }

    public static bool IsFinished(string? status) => FinishedStatuses.Contains(status, StringComparer.OrdinalIgnoreCase);

    /// <summary>A failed or cancelled import fails the step; success and partial (some rows not imported) complete it.</summary>
    public static void EnsureSucceeded(ImportRunStatusResponse status)
    {
        if (!string.Equals(status.Status, ImportRunStatus.Failed, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status.Status, ImportRunStatus.Cancelled, StringComparison.OrdinalIgnoreCase)) return;
        var detail = string.IsNullOrWhiteSpace(status.ErrorDetail) ? "" : $": {status.ErrorDetail.Trim()}";
        throw new PipelineNonRetryableException($"The import {status.Status.ToLowerInvariant()}{detail} (import run {status.RunId}).");
    }

    /// <summary>The step's output, read by later steps as Records Processed / Added / Updated / Unchanged / Errored / Skipped,
    /// Import Status and Import Run ID.</summary>
    public static string Output(ImportRunStatusResponse status) => JsonSerializer.Serialize(new
    {
        RecordsProcessed = status.RowsRead,
        RecordsAdded = status.Inserted,
        RecordsUpdated = status.Updated,
        RecordsUnchanged = status.Unchanged,
        RecordsErrored = status.Errored,
        RecordsSkipped = status.Skipped,
        ImportStatus = status.Status,
        ImportRunId = status.RunId
    });

    /// <summary>The same token for the same step of the same PowerFlow run (and loop iteration): a retry or resume gets the
    /// run it already started instead of a second import.</summary>
    public static string ClientToken(Guid messageId, Guid stepPublicId, string executionPath) =>
        "pf-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{messageId}|{stepPublicId}|{executionPath}"))).ToLowerInvariant();

    public static string RunReceipt(ImportWithCsvRunReceipt receipt) => JsonSerializer.Serialize(receipt);

    public static bool TryReadRunReceipt(string? json, out ImportWithCsvRunReceipt receipt)
    {
        receipt = null!;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            var read = JsonSerializer.Deserialize<ImportWithCsvRunReceipt>(json);
            if (read is null || read.ImportRunId == Guid.Empty) return false;
            receipt = read;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Starts the import with the file. Anything the import refuses (no permission, a template that needs attention, a
    /// run already going, a file it cannot read…) fails the step with the import's own message: retrying cannot fix it.</summary>
    public async Task<ImportRunStart> StartAsync(Guid appId, Guid templateId, ImportCsvFile file, string? notifyEmails, string? clientToken, string? baseUrl, CancellationToken ct)
    {
        var handler = _services.GetRequiredService<StartPowerFlowImportHandler>();
        try
        {
            return await handler.HandleAsync(appId, templateId, file.Content, file.FileName, file.Length,
                string.IsNullOrWhiteSpace(notifyEmails) ? null : notifyEmails, clientToken, baseUrl, ct);
        }
        catch (NotFoundException)
        {
            throw new PipelineNonRetryableException("The import template was not found in this app. It may have been deleted: open the step and select an import.");
        }
        catch (DomainException ex) when (ex is not PipelineNonRetryableException)
        {
            throw new PipelineNonRetryableException($"The import could not start: {ex.Message}");
        }
    }

    public async Task<ImportRunStatusResponse> GetStatusAsync(Guid appId, Guid runId, CancellationToken ct)
    {
        try
        {
            return await _services.GetRequiredService<GetImportRunStatusHandler>().HandleAsync(appId, runId, ct);
        }
        catch (NotFoundException)
        {
            throw new PipelineNonRetryableException($"The import run {runId} is no longer available.");
        }
        catch (DomainException ex) when (ex is not PipelineNonRetryableException)
        {
            throw new PipelineNonRetryableException($"The import run could not be read: {ex.Message}");
        }
    }

    /// <summary>Opens the file to import: the upload made in the step, or what the file reference resolved to — a file value
    /// from an earlier step (its JSON with name and path), a stored file path, or an http(s) URL.</summary>
    public async Task<ImportCsvFile> OpenFileAsync(ImportWithCsvDefinition config, string? resolvedSource, CancellationToken ct)
    {
        string path;
        string? name;
        if (config.HasUpload)
        {
            (path, name) = ReadFileValue(config.CsvUpload!)
                ?? throw new PipelineNonRetryableException("The file uploaded in the step is no longer available. Upload it again.");
        }
        else
        {
            var value = resolvedSource?.Trim() ?? "";
            if (value.Length == 0)
                throw new PipelineNonRetryableException("The CSV file is empty: the URL or the file from the earlier step had no value.");
            (path, name) = ReadFileValue(value) ?? (value, null);
        }

        if (path.StartsWith("/files/", StringComparison.OrdinalIgnoreCase)) return await OpenStoredAsync(path, name, ct);
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && string.IsNullOrEmpty(uri.UserInfo))
            return await DownloadAsync(uri, name, ct);
        throw new PipelineNonRetryableException("The CSV file must be a file uploaded in the step, an http(s) URL, or a file from an earlier step.");
    }

    /// <summary>The name and path of a file value: the JSON a file field or the file input holds (one file, or the first of
    /// several). Null when the value is not such JSON.</summary>
    public static (string Path, string? Name)? ReadFileValue(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('[')) return null;
        try
        {
            using var document = JsonDocument.Parse(trimmed);
            var file = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().FirstOrDefault(e => e.ValueKind == JsonValueKind.Object)
                : document.RootElement;
            if (file.ValueKind != JsonValueKind.Object) return null;
            string? Read(string property) => file.EnumerateObject()
                .FirstOrDefault(p => p.Name.Equals(property, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String).Value
                is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
            var path = Read("path") ?? Read("url");
            return string.IsNullOrWhiteSpace(path) ? null : (path, Read("name"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<ImportCsvFile> OpenStoredAsync(string path, string? name, CancellationToken ct)
    {
        var fileName = name ?? Path.GetFileName(path);
        foreach (var storage in new[] { _services.GetService<IFileStorageService>(), _fallbackStorage }.Distinct())
        {
            if (storage is not IFileStorageReadService readable) continue;
            Stream source;
            try { source = await readable.OpenReadAsync(path, ct); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or InvalidOperationException)
            {
                continue;
            }
            await using (source)
            {
                var copy = await CopyToTempAsync(source, ct);
                return new ImportCsvFile { Content = copy, FileName = ImportFileName(fileName, null, copy) };
            }
        }
        throw new PipelineNonRetryableException("The CSV file could not be found in file storage. Upload it again or select another file.");
    }

    private async Task<ImportCsvFile> DownloadAsync(Uri uri, string? name, CancellationToken ct)
    {
        using var response = await _download(uri, ct);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var message = $"The CSV file could not be downloaded: {uri.GetLeftPart(UriPartial.Path)} answered {status} ({response.ReasonPhrase}).";
            // A server error or a rate limit may pass; a missing or forbidden file will not.
            if (status >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests) throw new HttpRequestException(message, null, response.StatusCode);
            throw new PipelineNonRetryableException(message);
        }
        if (response.Content.Headers.ContentLength > ImportFileFormats.MaxFileBytes) throw TooLarge();
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        var copy = await CopyToTempAsync(source, ct);
        var disposition = response.Content.Headers.ContentDisposition;
        var fromHeader = (disposition?.FileNameStar ?? disposition?.FileName)?.Trim('"');
        var fromUrl = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        var fileName = !string.IsNullOrWhiteSpace(fromHeader) ? fromHeader
            : !string.IsNullOrWhiteSpace(Path.GetExtension(fromUrl)) ? fromUrl
            : name ?? fromUrl;
        return new ImportCsvFile { Content = copy, FileName = ImportFileName(fileName, response.Content.Headers.ContentType?.MediaType, copy) };
    }

    /// <summary>A temporary copy (deleted when closed), so the import knows the length and the job never holds a large file in memory.</summary>
    private static async Task<Stream> CopyToTempAsync(Stream source, CancellationToken ct)
    {
        var temp = new FileStream(Path.Combine(Path.GetTempPath(), "pb-import-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > ImportFileFormats.MaxFileBytes) throw TooLarge();
                await temp.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            if (total == 0) throw new PipelineNonRetryableException("The CSV file is empty.");
            temp.Position = 0;
            return temp;
        }
        catch
        {
            await temp.DisposeAsync();
            throw;
        }
    }

    /// <summary>A name the import recognises: .csv and .xlsx (and .xls, which the import explains how to convert) are kept;
    /// otherwise a workbook (a zip) becomes .xlsx and anything else .csv.</summary>
    public static string ImportFileName(string? name, string? mediaType, Stream content)
    {
        var fileName = Path.GetFileName((name ?? "").Trim().Trim('"'));
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "import";
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is ".csv" or ".xlsx" or ".xls") return fileName;
        var header = new byte[4];
        var position = content.Position;
        var read = content.Read(header, 0, header.Length);
        content.Position = position;
        var isWorkbook = (read == 4 && header[0] == 0x50 && header[1] == 0x4B && header[2] == 0x03 && header[3] == 0x04)
            || (mediaType?.Contains("spreadsheetml", StringComparison.OrdinalIgnoreCase) ?? false);
        return fileName + (isWorkbook ? ".xlsx" : ".csv");
    }

    private static PipelineNonRetryableException TooLarge() =>
        new($"The CSV file is larger than {ImportFileFormats.MaxFileBytes / (1024 * 1024)} MB. Split it into smaller files.");
}
