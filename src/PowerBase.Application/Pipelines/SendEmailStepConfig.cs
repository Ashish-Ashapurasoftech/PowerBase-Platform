using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Pipelines;

// Accept both saved legacy editor keys and the canonical mail contract.
public sealed class SendEmailStepConfig
{
    public string? ToAddresses { get; set; }
    public string? To { get; set; }
    public string? EmailTo { get; set; }
    public string? FromAddress { get; set; }
    public string? From { get; set; }
    public string? EmailFrom { get; set; }
    public string? CcAddresses { get; set; }
    public string? Cc { get; set; }
    public string? EmailCc { get; set; }
    public string? BccAddresses { get; set; }
    public string? Bcc { get; set; }
    public string? EmailBcc { get; set; }
    public string? Subject { get; set; }
    public string? EmailSubject { get; set; }
    public string? Body { get; set; }
    public string? EmailBody { get; set; }
    public string? SharedMailbox { get; set; }
    public string? ContentType { get; set; }
    public string? Importance { get; set; }
    public string? SaveToSentItems { get; set; }
    public List<string>? Attachments { get; set; }
    public string? AttachmentUrl { get; set; }
    public string? AttachmentFileName { get; set; }
    public string? AttachmentMimeType { get; set; }
    public string? UploadedAttachments { get; set; }
    public string? AttachmentReferences { get; set; }

    public void Normalize()
    {
        // Empty canonical values deliberately override stale aliases.
        ToAddresses ??= EmailTo ?? To;
        FromAddress ??= EmailFrom ?? From;
        CcAddresses ??= EmailCc ?? Cc;
        BccAddresses ??= EmailBcc ?? Bcc;
        Subject ??= EmailSubject;
        Body ??= EmailBody;
    }

    public IReadOnlyList<PipelineEmailAttachment> ResolveUrlAttachments(Func<string?, string> resolve)
    {
        static string[] Lines(string value) => value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(line => line.Trim()).ToArray();
        var urls = Lines(resolve(AttachmentUrl));
        var names = Lines(resolve(AttachmentFileName));
        var types = Lines(resolve(AttachmentMimeType));
        if (urls.All(string.IsNullOrWhiteSpace))
        {
            if (names.Any(value => !string.IsNullOrWhiteSpace(value)) || types.Any(value => !string.IsNullOrWhiteSpace(value)))
                throw new InvalidOperationException("Attachment URL is required when a file name or MIME type is specified.");
            return [];
        }
        if (urls.Count(url => !string.IsNullOrWhiteSpace(url)) > 50)
            throw new InvalidOperationException("An email may contain at most 50 URL attachments.");
        if (names.Skip(urls.Length).Any(value => !string.IsNullOrWhiteSpace(value)) || types.Skip(urls.Length).Any(value => !string.IsNullOrWhiteSpace(value)))
            throw new InvalidOperationException("Attachment names and MIME types must correspond to URL lines.");
        var result = new List<PipelineEmailAttachment>();
        for (var index = 0; index < urls.Length; index++)
        {
            var name = names.ElementAtOrDefault(index);
            var type = types.ElementAtOrDefault(index);
            if (string.IsNullOrWhiteSpace(urls[index]))
            {
                if (!string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(type))
                    throw new InvalidOperationException("Each attachment name and MIME type requires a URL on the same line.");
                continue;
            }
            result.Add(new(urls[index], name, type));
        }
        return result;
    }

    public IReadOnlyList<StoredFile> ResolveStoredAttachments(Func<string?, string> resolve)
    {
        var result = new List<StoredFile>();
        AddStoredFiles(result, resolve(UploadedAttachments));
        var references = (AttachmentReferences ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (var reference in references.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            AddStoredFiles(result, resolve(reference));
        if (result.Count > 50)
            throw new InvalidOperationException("An email may contain at most 50 uploaded or previous-step attachments.");
        return result;
    }

    private static void AddStoredFiles(List<StoredFile> result, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(value);
            if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in document.RootElement.EnumerateArray()) AddStoredFile(result, item);
            }
            else AddStoredFile(result, document.RootElement);
        }
        catch (System.Text.Json.JsonException)
        {
            // A previous step may expose only the stored path. Keep the display name safe and useful.
            result.Add(new StoredFile { Name = Path.GetFileName(value), Path = value.Trim() });
        }
    }

    private static void AddStoredFile(List<StoredFile> result, System.Text.Json.JsonElement item)
    {
        if (item.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            AddStoredFiles(result, item.GetString());
            return;
        }
        if (item.ValueKind != System.Text.Json.JsonValueKind.Object) return;
        static string? Read(System.Text.Json.JsonElement source, string name) =>
            source.TryGetProperty(name, out var value) ? value.GetString() :
            source.TryGetProperty(char.ToUpperInvariant(name[0]) + name[1..], out value) ? value.GetString() : null;
        var path = Read(item, "path");
        if (string.IsNullOrWhiteSpace(path)) return;
        result.Add(new StoredFile {
            Path = path,
            Name = Read(item, "name") ?? Path.GetFileName(path),
            ContentType = Read(item, "type") ?? Read(item, "contentType")
        });
    }
}
