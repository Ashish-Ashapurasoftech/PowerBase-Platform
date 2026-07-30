namespace PowerBase.Application.Import.Qbl;

public enum ImportDocumentFormat
{
    Pbl,
    Qbl,
}

/// <summary>Content-sniffs an uploaded import file rather than trusting its extension — a
/// renamed file shouldn't silently misparse. PBL is always a JSON object; QBL is always YAML,
/// which for a well-formed document never starts with <c>{</c>.</summary>
public static class DocumentFormatDetector
{
    public static ImportDocumentFormat Detect(string content)
    {
        var trimmed = content.AsSpan().TrimStart();
        return trimmed.Length > 0 && trimmed[0] == '{' ? ImportDocumentFormat.Pbl : ImportDocumentFormat.Qbl;
    }
}
