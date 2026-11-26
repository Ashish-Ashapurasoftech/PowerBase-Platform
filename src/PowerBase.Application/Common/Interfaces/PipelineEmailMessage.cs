namespace PowerBase.Application.Common.Interfaces;

public sealed record PipelineEmailMessage(
    string To, string Subject, string Body, string? Cc = null, string? Bcc = null,
    string? From = null, string? SharedMailbox = null, string ContentType = "HTML",
    string Importance = "Normal", string SaveToSentItems = "Yes",
    IEnumerable<string>? Attachments = null,
    IReadOnlyList<PipelineEmailAttachment>? UrlAttachments = null,
    IReadOnlyList<PipelineEmailContentAttachment>? ContentAttachments = null,
    bool RequireContent = true,
    long MaxAttachmentBytes = PipelineEmailLimits.SMTP_MAX_ATTACHMENT_BYTES);

/// <summary>Total attachment size per email. SMTP keeps 25 MB; Microsoft Graph accepts up to 150 MB per attachment (upload session).</summary>
public static class PipelineEmailLimits
{
    public const long SMTP_MAX_ATTACHMENT_BYTES = 25L * 1024 * 1024;
    public const long OUTLOOK_MAX_ATTACHMENT_BYTES = 150L * 1024 * 1024;

    public static string TooLargeMessage(long maxBytes) => $"Email attachments exceed the {maxBytes / (1024 * 1024)} MB total size limit.";
}

public sealed record PipelineEmailAttachment(string Url, string? FileName = null, string? MimeType = null);
public sealed record PipelineEmailContentAttachment(string FileName, string? MimeType, byte[] Content);
