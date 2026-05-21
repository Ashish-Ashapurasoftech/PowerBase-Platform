namespace PowerBase.Application.Common.Interfaces;

public interface IEmailService
{
    Task SendInvitationEmailAsync(string toEmail, string tenantName, string inviterName, CancellationToken ct = default);
    Task SendInviteSetupEmailAsync(string toEmail, string tenantName, string inviterName, string setupLink, CancellationToken ct = default);
}
