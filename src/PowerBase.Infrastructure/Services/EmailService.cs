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
}
