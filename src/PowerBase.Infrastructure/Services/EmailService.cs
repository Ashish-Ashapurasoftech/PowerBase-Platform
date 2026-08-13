using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Infrastructure.Services;

public class EmailService : IEmailService
{
    private readonly IConfiguration _config;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IConfiguration config, ILogger<EmailService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task SendInvitationEmailAsync(string toEmail, string tenantName, string inviterName, CancellationToken ct = default)
    {
        var subject = $"You've been invited to {tenantName} on PowerBase";
        var body = $"""
            Hi,

            {inviterName} has invited you to join the workspace "{tenantName}" on PowerBase.

            Log in at your PowerBase account to access this workspace.

            — The PowerBase Team
            """;

        var smtpHost = _config["Email:SmtpHost"];
        if (string.IsNullOrWhiteSpace(smtpHost))
        {
            _logger.LogInformation(
                "[EmailService DEV] To: {To} | Subject: {Subject}\n{Body}",
                toEmail, subject, body);
            return;
        }

        var smtpPort = int.TryParse(_config["Email:SmtpPort"], out var p) ? p : 587;
        var fromAddress = _config["Email:FromAddress"] ?? "noreply@powerbase.io";
        var fromName = _config["Email:FromName"] ?? "PowerBase";
        var username = _config["Email:Username"];
        var password = _config["Email:Password"];

        using var client = new SmtpClient(smtpHost, smtpPort)
        {
            EnableSsl = true,
            Credentials = !string.IsNullOrWhiteSpace(username)
                ? new NetworkCredential(username, password)
                : null,
        };

        using var message = new MailMessage(
            new MailAddress(fromAddress, fromName),
            new MailAddress(toEmail))
        {
            Subject = subject,
            Body = body,
            IsBodyHtml = false,
        };

        await client.SendMailAsync(message, ct);
    }

    public async Task SendInviteSetupEmailAsync(string toEmail, string tenantName, string inviterName, string setupLink, CancellationToken ct = default)
    {
        var subject = $"You've been invited to {tenantName} on PowerBase";
        var body = $"""
            Hi,

            {inviterName} has invited you to join "{tenantName}" on PowerBase.

            To accept this invitation and set up your account, click the link below:
            {setupLink}

            This link expires in 7 days.

            — The PowerBase Team
            """;

        var smtpHost = _config["Email:SmtpHost"];
        if (string.IsNullOrWhiteSpace(smtpHost))
        {
            _logger.LogInformation(
                "[EmailService DEV] To: {To} | Subject: {Subject}\n{Body}",
                toEmail, subject, body);
            return;
        }

        var smtpPort = int.TryParse(_config["Email:SmtpPort"], out var p) ? p : 587;
        var fromAddress = _config["Email:FromAddress"] ?? "noreply@powerbase.io";
        var fromName = _config["Email:FromName"] ?? "PowerBase";
        var username = _config["Email:Username"];
        var password = _config["Email:Password"];

        using var client = new SmtpClient(smtpHost, smtpPort)
        {
            EnableSsl = true,
            Credentials = !string.IsNullOrWhiteSpace(username)
                ? new NetworkCredential(username, password)
                : null,
        };

        using var message = new MailMessage(
            new MailAddress(fromAddress, fromName),
            new MailAddress(toEmail))
        {
            Subject = subject,
            Body = body,
            IsBodyHtml = false,
        };

        await client.SendMailAsync(message, ct);
    }

    public async Task SendPasswordResetEmailAsync(string toEmail, string resetLink, CancellationToken ct = default)
    {
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

        var smtpHost = _config["Email:SmtpHost"];
        if (string.IsNullOrWhiteSpace(smtpHost))
        {
            _logger.LogInformation(
                "[EmailService DEV] To: {To} | Subject: {Subject}\n{Body}",
                toEmail, subject, body);
            return;
        }

        var smtpPort = int.TryParse(_config["Email:SmtpPort"], out var p) ? p : 587;
        var fromAddress = _config["Email:FromAddress"] ?? "noreply@powerbase.io";
        var fromName = _config["Email:FromName"] ?? "PowerBase";
        var username = _config["Email:Username"];
        var password = _config["Email:Password"];

        using var client = new SmtpClient(smtpHost, smtpPort)
        {
            EnableSsl = true,
            Credentials = !string.IsNullOrWhiteSpace(username)
                ? new NetworkCredential(username, password)
                : null,
        };

        using var message = new MailMessage(
            new MailAddress(fromAddress, fromName),
            new MailAddress(toEmail))
        {
            Subject = subject,
            Body = body,
            IsBodyHtml = true,
        };

        await client.SendMailAsync(message, ct);
    }

    public async Task SendEmailAsync(string toEmail, string subject, string body, bool isHtml = false, CancellationToken ct = default)
    {
        var smtpHost = _config["Email:SmtpHost"];
        if (string.IsNullOrWhiteSpace(smtpHost))
        {
            _logger.LogInformation("[EmailService DEV] To: {To} | Subject: {Subject}\n{Body}", toEmail, subject, body);
            return;
        }

        var smtpPort = int.TryParse(_config["Email:SmtpPort"], out var p) ? p : 587;
        var fromAddress = _config["Email:FromAddress"] ?? "noreply@powerbase.io";
        var fromName = _config["Email:FromName"] ?? "PowerBase";
        var username = _config["Email:Username"];
        var password = _config["Email:Password"];

        using var client = new SmtpClient(smtpHost, smtpPort)
        {
            EnableSsl = true,
            Credentials = !string.IsNullOrWhiteSpace(username) ? new NetworkCredential(username, password) : null,
        };

        using var message = new MailMessage(new MailAddress(fromAddress, fromName), new MailAddress(toEmail))
        {
            Subject = subject,
            Body = body,
            IsBodyHtml = isHtml,
        };

        await client.SendMailAsync(message, ct);
    }
}
