namespace PowerBase.Application.Common.Interfaces;

public sealed record PipelineEmailMessage(
    string To, string Subject, string Body, string? Cc = null, string? Bcc = null,
    string? From = null, string? SharedMailbox = null, string ContentType = "HTML",
    string Importance = "Normal", string SaveToSentItems = "Yes",
    IEnumerable<string>? Attachments = null,
    IReadOnlyList<PipelineEmailAttachment>? UrlAttachments = null,
    bool RequireContent = true);

public sealed record PipelineEmailAttachment(string Url, string? FileName = null, string? MimeType = null);
