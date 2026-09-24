using System.Net;
using System.Net.Mail;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Infrastructure.Services;

public partial class EmailService
{
    public async Task SendPipelineEmailAsync(PipelineEmailMessage email, CancellationToken ct = default)
    {
        // Use the same server-side account as invitation and password-reset mail.
        // Never accept transport credentials from pipeline configuration.
        var host = _config["Email:SmtpHost"];
        if (string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException("Email sending is unavailable: the mail server is not configured.");

        using var message = CreatePipelineMessage(email, _config["Email:FromAddress"] ?? "noreply@powerbase.io", _config["Email:FromName"] ?? "PowerBase");
        if (email.UrlAttachments is { Count: > 0 })
        {
            using var downloader = EmailAttachmentDownloader.CreateClient();
            await EmailAttachmentDownloader.AddAsync(message, email.UrlAttachments, downloader, ct);
        }
        using var client = new SmtpClient(host, int.TryParse(_config["Email:SmtpPort"], out var port) ? port : 587)
        {
            EnableSsl = true,
            Credentials = !string.IsNullOrWhiteSpace(_config["Email:Username"])
                ? new NetworkCredential(_config["Email:Username"], _config["Email:Password"]) : null
        };
        await client.SendMailAsync(message, ct);
        _logger.LogInformation("Pipeline email accepted by the configured mail server.");
    }

    internal static MailMessage CreatePipelineMessage(PipelineEmailMessage email, string defaultFrom, string fromName)
    {
        var contentType = email.ContentType.Trim().ToLowerInvariant();
        if (contentType is not ("html" or "plain text" or "text"))
            throw new InvalidOperationException("Email content type must be HTML or Plain Text.");
        var priority = email.Importance.Trim().ToLowerInvariant() switch
        {
            "low" => MailPriority.Low, "normal" => MailPriority.Normal, "high" => MailPriority.High,
            _ => throw new InvalidOperationException("Email importance must be Low, Normal, or High.")
        };
        var sender = !string.IsNullOrWhiteSpace(email.SharedMailbox) ? email.SharedMailbox
            : !string.IsNullOrWhiteSpace(email.From) ? email.From : defaultFrom;
        var message = new MailMessage();
        try
        {
            message.From = new MailAddress(sender.Trim(), fromName);
            AddRecipients(message.To, email.To);
            AddRecipients(message.CC, email.Cc);
            AddRecipients(message.Bcc, email.Bcc);
            if (message.To.Count == 0) throw new InvalidOperationException("Email requires at least one To address.");
            if (email.RequireContent && (string.IsNullOrWhiteSpace(email.Subject) || string.IsNullOrWhiteSpace(email.Body)))
                throw new InvalidOperationException("Email subject and body are required.");
            message.Subject = email.Subject;
            message.IsBodyHtml = contentType == "html";
            message.Body = message.IsBodyHtml && !LooksLikeHtml(email.Body)
                ? WebUtility.HtmlEncode(email.Body).Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "<br>\r\n")
                : email.Body;
            message.Priority = priority;
            long attachmentSize = 0;
            foreach (var path in email.Attachments ?? [])
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (!File.Exists(path)) throw new InvalidOperationException("An email attachment could not be found.");
                attachmentSize += new FileInfo(path).Length;
                if (attachmentSize > EmailAttachmentDownloader.MaxBytes)
                    throw new InvalidOperationException("Email attachments exceed the 25 MB total size limit.");
                message.Attachments.Add(new Attachment(path));
            }
            // SMTP has no Sent Items operation. Retention is controlled by the provider.
            return message;
        }
        catch
        {
            message.Dispose();
            throw;
        }
    }

    private static bool LooksLikeHtml(string value) => Regex.IsMatch(
        value,
        @"<\s*(?:!doctype|html|body|p|div|span|br|table|tr|td|th|h[1-6]|a|ul|ol|li|style|strong|b|em|i)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static void AddRecipients(MailAddressCollection recipients, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        // Preserve commas within quoted display names, e.g. "Doe, Jane" <jane@example.com>.
        var address = new System.Text.StringBuilder();
        var quoted = false;
        var escaped = false;
        foreach (var character in value)
        {
            if (character == '"' && !escaped) quoted = !quoted;
            if (!quoted && character is ',' or ';' or '\r' or '\n')
            {
                if (!string.IsNullOrWhiteSpace(address.ToString())) recipients.Add(new MailAddress(address.ToString().Trim()));
                address.Clear();
            }
            else address.Append(character);
            escaped = character == '\\' && !escaped;
        }
        if (!string.IsNullOrWhiteSpace(address.ToString())) recipients.Add(new MailAddress(address.ToString().Trim()));
    }
}
