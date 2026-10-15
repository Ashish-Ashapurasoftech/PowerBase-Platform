using System.Text.Json;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pipelines;

/// <summary>
/// The saved configuration of the Import with CSV step: a saved import of the app (an "import template" that reads a file,
/// with its column mapping and merge key) and the file to give it — uploaded from the computer, or a URL / a file from an
/// earlier step. The step starts that import exactly as the import run API does, then waits for it to finish.
/// </summary>
public sealed class ImportWithCsvDefinition
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public string? App { get; set; }
    public string? AppId { get; set; }
    public string? AppPublicId { get; set; }
    public string? TableLabel { get; set; }
    public string? TableId { get; set; }
    public string? TablePublicId { get; set; }
    public string? ImportTemplateId { get; set; }
    /// <summary>A URL, or a reference to a file (or URL) from an earlier step.</summary>
    public string? CsvFile { get; set; }
    /// <summary>The URL field of the step before it was based on import templates; read when <see cref="CsvFile"/> is empty.</summary>
    public string? CsvUrl { get; set; }
    /// <summary>A file uploaded in the step (the file input's JSON: name and stored path). Used before <see cref="CsvFile"/>.</summary>
    public string? CsvUpload { get; set; }
    public string? NotifyEmails { get; set; }

    public static ImportWithCsvDefinition Read(string? json)
    {
        try
        {
            return JsonSerializer.Deserialize<ImportWithCsvDefinition>(string.IsNullOrWhiteSpace(json) ? "{}" : json, Options) ?? new();
        }
        catch (JsonException)
        {
            throw new PipelineNonRetryableException("The Import with CSV configuration is invalid.");
        }
    }

    public Guid AppGuid => ParseId(AppPublicId ?? App ?? AppId)
        ?? throw new PipelineNonRetryableException("Import with CSV requires an app.");

    public Guid TemplateGuid => ParseId(ImportTemplateId)
        ?? throw new PipelineNonRetryableException("Import with CSV requires an import template. Open the step and select one.");

    /// <summary>The file reference to resolve when no file was uploaded in the step.</summary>
    public string? FileSource => !string.IsNullOrWhiteSpace(CsvFile) ? CsvFile : CsvUrl;

    public bool HasUpload => !string.IsNullOrWhiteSpace(CsvUpload);

    public void Validate()
    {
        _ = AppGuid;
        _ = TemplateGuid;
        if (!HasUpload && string.IsNullOrWhiteSpace(FileSource))
            throw new PipelineNonRetryableException("Import with CSV requires a CSV or Excel file: upload one, enter its URL, or select a file from an earlier step.");
    }

    /// <summary>An id as the editor saves it: a plain GUID, or "context:GUID".</summary>
    private static Guid? ParseId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Guid.TryParse(value.Split(':').Last(), out var id) && id != Guid.Empty ? id : null;
}
