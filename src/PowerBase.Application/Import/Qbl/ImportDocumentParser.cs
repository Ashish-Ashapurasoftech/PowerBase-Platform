using PowerBase.Application.Import.Pbl;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Import.Qbl;

/// <summary>One application found in an uploaded file, already converted to PBL. <see cref="AppRef"/> is the
/// application's logical ref (unique within the file); table refs are only unique inside one application.</summary>
public sealed record ParsedApp(string AppRef, PblDocument Document, List<PblIssue> Issues);

/// <summary>
/// Single entry point <see cref="ImportPreviewQueryHandler"/>/<see cref="ImportAppFromPblCommandHandler"/>
/// use to turn raw uploaded content — PBL JSON or QBL YAML, auto-detected — into
/// <see cref="PblDocument"/>s. Everything downstream of this call only ever sees PBL.
/// </summary>
public static class ImportDocumentParser
{
    public const string MULTIPLE_APPS_NOT_ALLOWED_FOR_UPDATE = "IMPORT_MULTIPLE_APPS_NOT_ALLOWED_FOR_UPDATE";

    private const int MAX_NAMES_IN_MESSAGE = 5;

    /// <summary>Throws <see cref="System.Text.Json.JsonException"/> for malformed PBL input or
    /// <see cref="YamlDotNet.Core.YamlException"/> for malformed QBL input — callers keep their
    /// existing per-format catch blocks. A QBL file yields one entry per application, in file order; PBL always one.</summary>
    public static IReadOnlyList<ParsedApp> ParseAll(string content)
    {
        if (DocumentFormatDetector.Detect(content) == ImportDocumentFormat.Pbl)
        {
            var pbl = PblSerializer.Deserialize(content);
            return [new ParsedApp(pbl.App?.LogicalRef ?? string.Empty, pbl, [])];
        }

        var qblDocument = QblSerializer.Deserialize(content);
        return QblToPblConverter.ConvertAll(qblDocument)
            .Select(r => new ParsedApp(r.AppRef, r.Document, r.Issues))
            .ToList();
    }

    /// <summary>For flows that work on exactly one application (Update Existing App). A file holding several is
    /// rejected rather than silently using the first.</summary>
    public static (PblDocument Document, List<PblIssue> ConversionIssues) Parse(string content)
    {
        var apps = ParseAll(content);
        EnsureSingleApp(apps);
        return (apps[0].Document, apps[0].Issues);
    }

    public static void EnsureSingleApp(IReadOnlyList<ParsedApp> apps)
    {
        if (apps.Count > 1)
            throw new BadRequestException(MULTIPLE_APPS_NOT_ALLOWED_FOR_UPDATE, MultipleAppsMessage(apps));
    }

    public static string MultipleAppsMessage(IReadOnlyList<ParsedApp> apps)
    {
        var names = apps.Select(a => string.IsNullOrWhiteSpace(a.Document.App?.Name) ? a.AppRef : a.Document.App!.Name).ToList();
        var shown = string.Join(", ", names.Take(MAX_NAMES_IN_MESSAGE));
        if (names.Count > MAX_NAMES_IN_MESSAGE)
            shown += $" and {names.Count - MAX_NAMES_IN_MESSAGE} more";
        return $"This file contains {apps.Count} applications ({shown}). Update Existing App works with a file that contains exactly one application. " +
               "Export a single application from Quickbase and try again, or use New App import to create all of them.";
    }
}
