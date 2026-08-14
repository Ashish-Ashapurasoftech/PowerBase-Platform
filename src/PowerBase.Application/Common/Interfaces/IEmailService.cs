namespace PowerBase.Application.Common.Interfaces;

public interface IEmailService
{
    Task SendInvitationEmailAsync(string toEmail, string tenantName, string inviterName, CancellationToken ct = default);
    Task SendInviteSetupEmailAsync(string toEmail, string tenantName, string inviterName, string setupLink, CancellationToken ct = default);
    Task SendPasswordResetEmailAsync(string toEmail, string resetLink, CancellationToken ct = default);
    Task SendEmailAsync(string toEmail, string subject, string body, bool isHtml = false, CancellationToken ct = default);
}
