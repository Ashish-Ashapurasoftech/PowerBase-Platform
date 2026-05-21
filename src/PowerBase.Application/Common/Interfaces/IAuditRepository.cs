using System.Data;
using PowerBase.Application.Auth;

namespace PowerBase.Application.Common.Interfaces;

public interface IAuditRepository
{
    Task RecordLoginAttemptAsync(string emailAttempted, string ipAddress, bool wasSuccessful, long? userId = null, string? failureReason = null, CancellationToken ct = default);
    Task CreateSessionAsync(long userId, long tenantId, Guid jwtId, string ipAddress, DateTime expiresOn, IDbTransaction? transaction = null, CancellationToken ct = default);

    Task CreateInviteTokenAsync(long userId, long tenantId, long tenantRoleId, string tokenHash, DateTime expiresOn, long invitedBy, CancellationToken ct = default);
    Task<InviteTokenRecord?> GetInviteTokenByHashAsync(string tokenHash, CancellationToken ct = default);
    Task ConsumeInviteTokenAsync(long tokenId, CancellationToken ct = default);
}
