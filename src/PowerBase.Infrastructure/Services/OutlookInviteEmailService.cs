using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines.Outlook;

namespace PowerBase.Infrastructure.Services;

/// <summary>
/// Extra switchable service for sending invite & notification emails through Microsoft Outlook
/// (reusing the pipeline Microsoft Graph / Outlook integration), or delegating to the default EmailService.
/// 
/// If Email:UseCurrentEmail (or Email:UseDefaultSmtp) in appsettings.json is true -> uses current SMTP EmailService.
/// If false -> sends via Microsoft Outlook (Microsoft Graph API / Pipeline Outlook Connection).
/// 
/// To remove this feature in the future:
/// 1. Delete this file.
/// 2. In Program.cs, change builder.Services.AddScoped<IEmailService, ...> back to builder.Services.AddScoped<IEmailService, EmailService>().
/// </summary>
public class OutlookInviteEmailService : IEmailService
{
    private readonly EmailService _defaultEmailService;
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<OutlookInviteEmailService> _logger;

    public OutlookInviteEmailService(
        EmailService defaultEmailService,
        IConfiguration config,
        IHttpClientFactory httpClientFactory,
        IServiceProvider serviceProvider,
        ILogger<OutlookInviteEmailService> logger)
    {
        _defaultEmailService = defaultEmailService;
        _config = config;
        _httpClientFactory = httpClientFactory;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// Checks appsettings toggle:
    /// true  -> Use current SMTP email sender
    /// false -> Use Microsoft Outlook email sender
    /// </summary>
    private bool ShouldUseCurrentEmailService()
    {
        // Support both "Email:UseCurrentEmail" and "Email:UseDefaultSmtp"
        if (_config.GetSection("Email:UseCurrentEmail").Exists())
            return _config.GetValue<bool>("Email:UseCurrentEmail");

        if (_config.GetSection("Email:UseDefaultSmtp").Exists())
            return _config.GetValue<bool>("Email:UseDefaultSmtp");

        return true; // Default fallback to current email service
    }

    public async Task SendInviteSetupEmailAsync(string toEmail, string tenantName, string inviterName, string setupLink, CancellationToken ct = default)
    {
        if (ShouldUseCurrentEmailService())
        {
            await _defaultEmailService.SendInviteSetupEmailAsync(toEmail, tenantName, inviterName, setupLink, ct);
            return;
        }

        var subject = $"You've been invited to {tenantName} on PowerBase";
        var body = $"""
            Hi,

            {inviterName} has invited you to join "{tenantName}" on PowerBase.

            To accept this invitation and set up your account, click the link below:
            {setupLink}

            This link expires in 7 days.

            — The PowerBase Team
            """;

        await SendViaOutlookAsync(toEmail, subject, body, isHtml: false, ct);
    }

    public async Task SendInvitationEmailAsync(string toEmail, string tenantName, string inviterName, CancellationToken ct = default)
    {
        if (ShouldUseCurrentEmailService())
        {
            await _defaultEmailService.SendInvitationEmailAsync(toEmail, tenantName, inviterName, ct);
            return;
        }

        var subject = $"You've been invited to {tenantName} on PowerBase";
        var body = $"""
            Hi,

            {inviterName} has invited you to join the workspace "{tenantName}" on PowerBase.

            Log in at your PowerBase account to access this workspace.

            — The PowerBase Team
            """;

        await SendViaOutlookAsync(toEmail, subject, body, isHtml: false, ct);
    }

    public async Task SendPasswordResetEmailAsync(string toEmail, string resetLink, CancellationToken ct = default)
    {
        if (ShouldUseCurrentEmailService())
        {
            await _defaultEmailService.SendPasswordResetEmailAsync(toEmail, resetLink, ct);
            return;
        }

        var subject = "Reset your PowerBase password";
        var body = $"""
            <html>
            <body style="font-family: Arial, sans-serif; line-height: 1.6; color: #333; max-width: 600px; margin: 0 auto; padding: 20px;">
                <h2 style="color: #2c3e50;">Password Reset Request</h2>
                <p>Hello,</p>
                <p>We received a request to reset your password for your PowerBase account. If you didn't make this request, you can safely ignore this email.</p>
                <p>To reset your password, click the button below:</p>
                <div style="margin: 30px 0;">
                    <a href="{resetLink}" style="background-color: #d97706; color: white; padding: 12px 24px; text-decoration: none; border-radius: 6px; font-weight: bold; display: inline-block;">Reset Password</a>
                </div>
                <p>For security, this link will expire in 30 minutes.</p>
                <p>Best regards,<br>The PowerBase Team</p>
            </body>
            </html>
            """;

        await SendViaOutlookAsync(toEmail, subject, body, isHtml: true, ct);
    }

    public async Task SendEmailAsync(string toEmail, string subject, string body, bool isHtml = false, CancellationToken ct = default)
    {
        if (ShouldUseCurrentEmailService())
        {
            await _defaultEmailService.SendEmailAsync(toEmail, subject, body, isHtml, ct);
            return;
        }

        await SendViaOutlookAsync(toEmail, subject, body, isHtml, ct);
    }

    public async Task SendEmailAsync(string toEmail, string subject, string body, string? cc = null, string? bcc = null, IEnumerable<string>? attachmentPaths = null, string? fromAddress = null, CancellationToken ct = default)
    {
        if (ShouldUseCurrentEmailService())
        {
            await _defaultEmailService.SendEmailAsync(toEmail, subject, body, cc, bcc, attachmentPaths, fromAddress, ct);
            return;
        }

        await SendViaOutlookAsync(toEmail, subject, body, isHtml: false, ct, cc: cc, bcc: bcc);
    }

    public Task SendPipelineEmailAsync(PipelineEmailMessage message, CancellationToken ct = default)
    {
        return _defaultEmailService.SendPipelineEmailAsync(message, ct);
    }

    public Task SendRecursionAlertEmailAsync(long pipelineId, string correlationId, int depth, string message, CancellationToken ct = default)
    {
        return _defaultEmailService.SendRecursionAlertEmailAsync(pipelineId, correlationId, depth, message, ct);
    }

    /// <summary>
    /// Sends an email through Microsoft Outlook using Microsoft Graph API / Pipeline Outlook Connection.
    /// Supports:
    /// 1. Pipeline Outlook Connection ID (meta.MicrosoftConnection) via IOutlookConnectionService
    /// 2. Direct Microsoft Graph App Credentials (ClientId + ClientSecret + TenantId)
    /// 3. Fallback to Microsoft 365 SMTP (smtp.office365.com)
    /// </summary>
    private async Task SendViaOutlookAsync(
        string toEmail,
        string subject,
        string body,
        bool isHtml,
        CancellationToken ct,
        string? cc = null,
        string? bcc = null)
    {
        var senderEmail = _config["Email:Outlook:SenderEmail"] 
                       ?? _config["Email:FromAddress"] 
                       ?? _config["Microsoft:SenderEmail"];

        // -------------------------------------------------------------------------
        // Mode 1: Reusing Pipeline Outlook Connection ID (OAuth account in meta.MicrosoftConnection)
        // -------------------------------------------------------------------------
        var connectionIdStr = _config["Email:Outlook:ConnectionId"] ?? _config["Microsoft:ConnectionId"];
        if (Guid.TryParse(connectionIdStr, out var connectionGuid))
        {
            var outlookConnectionService = _serviceProvider.GetService<IOutlookConnectionService>();
            if (outlookConnectionService != null)
            {
                var ownerUserId = _config.GetValue<long?>("Email:Outlook:OwnerUserId") ?? 1;
                var pipelineMessage = new PipelineEmailMessage(
                    To: toEmail,
                    Subject: subject,
                    Body: body,
                    Cc: cc,
                    Bcc: bcc,
                    From: senderEmail,
                    SharedMailbox: null,
                    ContentType: isHtml ? "HTML" : "Plain Text",
                    Importance: "Normal",
                    SaveToSentItems: "Yes",
                    Attachments: null,
                    UrlAttachments: null,
                    ContentAttachments: null,
                    RequireContent: true
                );

                _logger.LogInformation("Sending invite email via Pipeline Outlook connection {ConnectionId} to {To}", connectionGuid, toEmail);
                await outlookConnectionService.SendAsync(connectionGuid, ownerUserId, pipelineMessage, ct);
                return;
            }
        }

        // -------------------------------------------------------------------------
        // Mode 2: Microsoft Graph API via Client Credentials (TenantId, ClientId, ClientSecret)
        // -------------------------------------------------------------------------
        var clientId = _config["Email:Outlook:ClientId"] ?? _config["Microsoft:ClientId"];
        var clientSecret = _config["Email:Outlook:ClientSecret"] ?? _config["Microsoft:ClientSecret"];
        var tenantId = _config["Email:Outlook:TenantId"] ?? _config["Microsoft:TenantId"];

        if (!string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret) && !string.IsNullOrWhiteSpace(tenantId))
        {
            if (string.IsNullOrWhiteSpace(senderEmail))
            {
                throw new InvalidOperationException("Email:Outlook:SenderEmail (or Email:FromAddress) must be configured to send via Microsoft Outlook Graph API.");
            }

            _logger.LogInformation("Sending invite email via Microsoft Graph API ({SenderEmail} -> {To})", senderEmail, toEmail);
            var token = await AcquireGraphAppTokenAsync(tenantId, clientId, clientSecret, ct);
            await SendGraphMailAsync(token, senderEmail, toEmail, subject, body, isHtml, cc, bcc, ct);
            return;
        }

        // -------------------------------------------------------------------------
        // Mode 3: Microsoft 365 Outlook SMTP (smtp.office365.com)
        // -------------------------------------------------------------------------
        var outlookPassword = _config["Email:Outlook:Password"] ?? _config["Email:Password"];
        if (!string.IsNullOrWhiteSpace(senderEmail) && !string.IsNullOrWhiteSpace(outlookPassword))
        {
            _logger.LogInformation("Sending invite email via Microsoft 365 SMTP ({SenderEmail} -> {To})", senderEmail, toEmail);
            using var smtp = new SmtpClient("smtp.office365.com", 587)
            {
                EnableSsl = true,
                Credentials = new NetworkCredential(senderEmail, outlookPassword)
            };

            using var mail = new MailMessage(senderEmail, toEmail)
            {
                Subject = subject,
                Body = body,
                IsBodyHtml = isHtml
            };
            if (!string.IsNullOrWhiteSpace(cc)) mail.CC.Add(cc);
            if (!string.IsNullOrWhiteSpace(bcc)) mail.Bcc.Add(bcc);

            await smtp.SendMailAsync(mail, ct);
            return;
        }

        // -------------------------------------------------------------------------
        // Fallback for Development: Log to console if credentials are not configured yet
        // -------------------------------------------------------------------------
        _logger.LogWarning("[OutlookInviteEmailService DEV - No Outlook credentials configured] To: {To} | Subject: {Subject}\n{Body}", toEmail, subject, body);
    }

    /// <summary>
    /// Acquires an application access token from Microsoft Entra ID (Azure AD) using Client Credentials grant.
    /// </summary>
    private async Task<string> AcquireGraphAppTokenAsync(string tenantId, string clientId, string clientSecret, CancellationToken ct)
    {
        using var client = _httpClientFactory.CreateClient();
        var tokenEndpoint = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";

        var payload = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["scope"] = "https://graph.microsoft.com/.default",
            ["grant_type"] = "client_credentials"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(payload)
        };

        using var response = await client.SendAsync(request, ct);
        var content = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to acquire Microsoft Graph token: {Error}", content);
            throw new InvalidOperationException($"Microsoft Graph token error: {response.StatusCode} - {content}");
        }

        using var doc = JsonDocument.Parse(content);
        return doc.RootElement.GetProperty("access_token").GetString() 
            ?? throw new InvalidOperationException("Microsoft token response did not contain access_token.");
    }

    /// <summary>
    /// Sends an email through the Microsoft Graph endpoint: POST https://graph.microsoft.com/v1.0/users/{senderEmail}/sendMail
    /// </summary>
    private async Task SendGraphMailAsync(
        string accessToken,
        string senderEmail,
        string toEmail,
        string subject,
        string body,
        bool isHtml,
        string? cc,
        string? bcc,
        CancellationToken ct)
    {
        using var client = _httpClientFactory.CreateClient();
        var endpoint = $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(senderEmail)}/sendMail";

        var toRecipients = toEmail.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(email => new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = email } });

        var message = new JsonObject
        {
            ["subject"] = subject,
            ["body"] = new JsonObject
            {
                ["contentType"] = isHtml ? "HTML" : "Text",
                ["content"] = body
            },
            ["toRecipients"] = new JsonArray(toRecipients.ToArray())
        };

        if (!string.IsNullOrWhiteSpace(cc))
        {
            var ccRecipients = cc.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(email => new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = email } });
            message["ccRecipients"] = new JsonArray(ccRecipients.ToArray());
        }

        if (!string.IsNullOrWhiteSpace(bcc))
        {
            var bccRecipients = bcc.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(email => new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = email } });
            message["bccRecipients"] = new JsonArray(bccRecipients.ToArray());
        }

        var requestBody = new JsonObject
        {
            ["message"] = message,
            ["saveToSentItems"] = true
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(requestBody.ToJsonString(), System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Microsoft Graph sendMail failed with HTTP {Status}: {Error}", response.StatusCode, err);
            throw new InvalidOperationException($"Microsoft Outlook (Graph) rejected email: {response.StatusCode} - {err}");
        }

        _logger.LogInformation("Invite email successfully sent via Microsoft Outlook Graph to {To}", toEmail);
    }
}
