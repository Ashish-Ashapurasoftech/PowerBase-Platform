namespace PowerBase.Application.Common.Interfaces;

public interface IEmailService
{
    Task SendInvitationEmailAsync(string toEmail, string tenantName, string inviterName, CancellationToken ct = default);
}
