using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines.Outlook;

namespace PowerBase.Infrastructure.Services;

/// <summary>
/// Sends pipeline mail through Microsoft Graph with a delegated access token.
/// Message validation (recipients, content type, importance, subject, attachment size) is shared
/// with the SMTP path through <see cref="EmailService.CreatePipelineMessage"/>.
/// </summary>
public sealed class OutlookGraphMailSender : IOutlookGraphMailSender
{
    public const string HTTP_CLIENT_NAME = "MicrosoftGraph";
    /// <summary>Graph sendMail accepts at most ~4 MB per request; above this the draft + upload-session path is used.</summary>
    internal const long INLINE_ATTACHMENT_LIMIT = 3 * 1024 * 1024;
    internal const int UPLOAD_CHUNK_BYTES = 320 * 1024 * 10;
    private const string PLACEHOLDER_SENDER = "connected-account@outlook.invalid";

    private readonly IHttpClientFactory _clients;
    private readonly ILogger<OutlookGraphMailSender> _logger;
    private readonly string _graphBaseUrl;

    public OutlookGraphMailSender(IHttpClientFactory clients, IConfiguration config, ILogger<OutlookGraphMailSender> logger)
    {
        _clients = clients;
        _logger = logger;
        _graphBaseUrl = (config["Microsoft:GraphBaseUrl"] ?? "https://graph.microsoft.com/v1.0").TrimEnd('/');
    }

    private sealed record GraphAttachment(string Name, string ContentType, byte[] Content);

    public async Task SendAsync(string accessToken, PipelineEmailMessage email, CancellationToken ct)
    {
        email = email with { MaxAttachmentBytes = PipelineEmailLimits.OUTLOOK_MAX_ATTACHMENT_BYTES };
        using var mail = EmailService.CreatePipelineMessage(email with { SharedMailbox = null, From = null }, PLACEHOLDER_SENDER, "");
        if (email.UrlAttachments is { Count: > 0 })
        {
            using var downloader = EmailAttachmentDownloader.CreateClient();
            await EmailAttachmentDownloader.AddAsync(mail, email.UrlAttachments, downloader, ct, email.MaxAttachmentBytes);
        }
        var attachments = new List<GraphAttachment>();
        foreach (var attachment in mail.Attachments)
        {
            using var buffer = new MemoryStream();
            await attachment.ContentStream.CopyToAsync(buffer, ct);
            attachments.Add(new(attachment.Name ?? "attachment", attachment.ContentType?.MediaType ?? "application/octet-stream", buffer.ToArray()));
        }
        if (attachments.Sum(a => (long)a.Content.Length) > email.MaxAttachmentBytes)
            throw new InvalidOperationException(PipelineEmailLimits.TooLargeMessage(email.MaxAttachmentBytes));

        var sharedMailbox = Clean(email.SharedMailbox);
        var fromAddress = Clean(email.From);
        if (sharedMailbox != null) _ = new MailAddress(sharedMailbox);
        if (fromAddress != null) _ = new MailAddress(fromAddress);
        var mailbox = sharedMailbox == null ? "me" : "users/" + Uri.EscapeDataString(sharedMailbox);
        var saveToSentItems = !string.Equals(email.SaveToSentItems?.Trim(), "No", StringComparison.OrdinalIgnoreCase);

        var message = BuildMessage(mail, fromAddress);
        var customSender = sharedMailbox != null || fromAddress != null;
        using var client = _clients.CreateClient(HTTP_CLIENT_NAME);
        var total = attachments.Sum(a => (long)a.Content.Length);
        if (total <= INLINE_ATTACHMENT_LIMIT)
        {
            if (attachments.Count > 0) message["attachments"] = new JsonArray(attachments.Select(FileAttachment).ToArray());
            var body = new JsonObject { ["message"] = message, ["saveToSentItems"] = saveToSentItems };
            await SendJsonAsync(client, HttpMethod.Post, $"{_graphBaseUrl}/{mailbox}/sendMail", accessToken, body, customSender, ct);
        }
        else
        {
            // Large attachments: create a draft, attach (upload sessions for big files), then send the draft.
            // A sent draft is always kept in Sent Items by Exchange, so SaveToSentItems = No cannot apply here.
            // Creating a draft needs Mail.ReadWrite (Mail.ReadWrite.Shared for a shared mailbox), not just Mail.Send.
            JsonNode? draft;
            try
            {
                draft = await SendJsonAsync(client, HttpMethod.Post, $"{_graphBaseUrl}/{mailbox}/messages", accessToken, message, customSender, ct);
            }
            catch (GraphAccessDeniedException) when (!customSender)
            {
                throw new InvalidOperationException(LARGE_ATTACHMENT_PERMISSION_MESSAGE);
            }
            var draftId = draft?["id"]?.GetValue<string>() ?? throw new InvalidOperationException("Microsoft Outlook did not create the email draft.");
            var draftUrl = $"{_graphBaseUrl}/{mailbox}/messages/{Uri.EscapeDataString(draftId)}";
            foreach (var attachment in attachments)
            {
                if (attachment.Content.Length < INLINE_ATTACHMENT_LIMIT)
                    await SendJsonAsync(client, HttpMethod.Post, draftUrl + "/attachments", accessToken, FileAttachment(attachment), customSender, ct);
                else
                    await UploadLargeAttachmentAsync(client, draftUrl, accessToken, attachment, customSender, ct);
            }
            await SendJsonAsync(client, HttpMethod.Post, draftUrl + "/send", accessToken, null, customSender, ct);
        }
        _logger.LogInformation("Pipeline email accepted by Microsoft Outlook ({Mailbox}).", sharedMailbox == null ? "connected account" : "shared mailbox");
    }

    internal static JsonObject BuildMessage(MailMessage mail, string? fromAddress)
    {
        static JsonArray Recipients(MailAddressCollection list) => new(list.Select(address => (JsonNode)new JsonObject
        {
            ["emailAddress"] = string.IsNullOrWhiteSpace(address.DisplayName)
                ? new JsonObject { ["address"] = address.Address }
                : new JsonObject { ["address"] = address.Address, ["name"] = address.DisplayName }
        }).ToArray());
        var message = new JsonObject
        {
            ["subject"] = mail.Subject,
            ["body"] = new JsonObject { ["contentType"] = mail.IsBodyHtml ? "HTML" : "Text", ["content"] = mail.Body },
            ["toRecipients"] = Recipients(mail.To),
            ["importance"] = mail.Priority switch { MailPriority.Low => "low", MailPriority.High => "high", _ => "normal" }
        };
        if (mail.CC.Count > 0) message["ccRecipients"] = Recipients(mail.CC);
        if (mail.Bcc.Count > 0) message["bccRecipients"] = Recipients(mail.Bcc);
        if (fromAddress != null) message["from"] = new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = fromAddress } };
        return message;
    }

    private static JsonNode FileAttachment(GraphAttachment attachment) => new JsonObject
    {
        ["@odata.type"] = "#microsoft.graph.fileAttachment",
        ["name"] = attachment.Name,
        ["contentType"] = attachment.ContentType,
        ["contentBytes"] = Convert.ToBase64String(attachment.Content)
    };

    private async Task UploadLargeAttachmentAsync(HttpClient client, string draftUrl, string accessToken, GraphAttachment attachment, bool customSender, CancellationToken ct)
    {
        var session = await SendJsonAsync(client, HttpMethod.Post, draftUrl + "/attachments/createUploadSession", accessToken, new JsonObject
        {
            ["AttachmentItem"] = new JsonObject
            {
                ["attachmentType"] = "file", ["name"] = attachment.Name, ["size"] = attachment.Content.Length, ["contentType"] = attachment.ContentType
            }
        }, customSender, ct);
        var uploadUrl = session?["uploadUrl"]?.GetValue<string>() ?? throw new InvalidOperationException("Microsoft Outlook did not open an attachment upload session.");
        for (var offset = 0; offset < attachment.Content.Length; offset += UPLOAD_CHUNK_BYTES)
        {
            var length = Math.Min(UPLOAD_CHUNK_BYTES, attachment.Content.Length - offset);
            // The pre-authorized upload URL must not carry the bearer token.
            using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = new ByteArrayContent(attachment.Content, offset, length) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + length - 1, attachment.Content.Length);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw await GraphErrorAsync(response, customSender, ct);
        }
    }

    private static async Task<JsonNode?> SendJsonAsync(HttpClient client, HttpMethod method, string url, string accessToken, JsonNode? body, bool customSender, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(body?.ToJsonString() ?? "", Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw await GraphErrorAsync(response, customSender, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    internal static async Task<Exception> GraphErrorAsync(HttpResponseMessage response, bool customSender, CancellationToken ct)
    {
        string? code = null, message = null;
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (json.RootElement.TryGetProperty("error", out var error))
            {
                code = error.TryGetProperty("code", out var c) ? c.GetString() : null;
                message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
            }
        }
        catch (JsonException) { }
        var detail = string.Join(": ", new[] { code, message }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var suffix = detail.Length > 0 ? " (" + detail + ")" : "";
        if (code == "ErrorMessageSizeExceeded" || response.StatusCode == System.Net.HttpStatusCode.RequestEntityTooLarge
            || (message?.Contains("size", StringComparison.OrdinalIgnoreCase) == true && message.Contains("exceed", StringComparison.OrdinalIgnoreCase)))
            return new InvalidOperationException(MAILBOX_SIZE_LIMIT_MESSAGE + suffix);
        if (code == "ErrorSendAsDenied" || (customSender && (code == "ErrorAccessDenied" || response.StatusCode == System.Net.HttpStatusCode.Forbidden)))
            return new GraphAccessDeniedException("Microsoft Outlook denied sending from this mailbox. The connected account needs Full Access plus Send As or Send on Behalf permission." + suffix);
        if (code == "ErrorAccessDenied" || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            return new GraphAccessDeniedException("Microsoft Outlook denied the request: the connected account has not granted the permission this email needs. Reconnect the Microsoft Outlook account and accept all permissions." + suffix);
        return new InvalidOperationException($"Microsoft Outlook rejected the email (HTTP {(int)response.StatusCode})" + (detail.Length > 0 ? ": " + detail : "."));
    }

    internal const string MAILBOX_SIZE_LIMIT_MESSAGE =
        "The email is larger than this Microsoft mailbox is allowed to send. Microsoft 365 mailboxes allow 35 MB by default; " +
        "a Microsoft 365 admin can raise the mailbox MaxSendSize up to 150 MB. Personal Outlook.com accounts have lower limits.";

    internal const string LARGE_ATTACHMENT_PERMISSION_MESSAGE =
        "Attachments larger than 3 MB need the \"Read and write user mail\" (Mail.ReadWrite) permission, which this Microsoft Outlook account has not granted. " +
        "Reconnect the account (Connect new account) and accept all permissions, or send smaller attachments.";

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Graph returned 403 / ErrorAccessDenied / ErrorSendAsDenied.</summary>
public sealed class GraphAccessDeniedException(string message) : InvalidOperationException(message);
