using PowerBase.Application.Import.Pbl;

namespace PowerBase.Application.Import.Qbl;

/// <summary>
/// Single entry point <see cref="ImportPreviewQueryHandler"/>/<see cref="ImportAppFromPblCommandHandler"/>
/// use to turn raw uploaded content — PBL JSON or QBL YAML, auto-detected — into a
/// <see cref="PblDocument"/>. Everything downstream of this call only ever sees PBL.
/// </summary>
public static class ImportDocumentParser
{
    /// <summary>Throws <see cref="System.Text.Json.JsonException"/> for malformed PBL input or
    /// <see cref="YamlDotNet.Core.YamlException"/> for malformed QBL input — callers keep their
    /// existing per-format catch blocks.</summary>
    public static (PblDocument Document, List<PblIssue> ConversionIssues) Parse(string content)
    {
        if (DocumentFormatDetector.Detect(content) == ImportDocumentFormat.Pbl)
            return (PblSerializer.Deserialize(content), []);

        var qblDocument = QblSerializer.Deserialize(content);
        var result = QblToPblConverter.Convert(qblDocument);
        return (result.Document, result.Issues);
    }
}
