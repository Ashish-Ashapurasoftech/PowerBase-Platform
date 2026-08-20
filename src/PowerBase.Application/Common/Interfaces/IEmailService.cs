namespace PowerBase.Application.Common.Interfaces;

public interface IEmailService
{
    Task SendInvitationEmailAsync(string toEmail, string tenantName, string inviterName, CancellationToken ct = default);
    Task SendInviteSetupEmailAsync(string toEmail, string tenantName, string inviterName, string setupLink, CancellationToken ct = default);
    Task SendPasswordResetEmailAsync(string toEmail, string resetLink, CancellationToken ct = default);
    Task SendEmailAsync(string toEmail, string subject, string body, bool isHtml = false, CancellationToken ct = default);
    Task SendEmailAsync(string toEmail, string subject, string body, string? cc = null, string? bcc = null, IEnumerable<string>? attachmentPaths = null, string? fromAddress = null, CancellationToken ct = default);
    Task SendRecursionAlertEmailAsync(long pipelineId, string correlationId, int depth, string message, CancellationToken ct = default);
}
