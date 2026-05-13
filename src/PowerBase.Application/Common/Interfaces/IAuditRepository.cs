using System.Data;

namespace PowerBase.Application.Common.Interfaces;

public interface IAuditRepository
{
    Task RecordLoginAttemptAsync(string emailAttempted, string ipAddress, bool wasSuccessful, long? userId = null, string? failureReason = null, CancellationToken ct = default);
    Task CreateSessionAsync(long userId, long tenantId, Guid jwtId, string ipAddress, DateTime expiresOn, IDbTransaction? transaction = null, CancellationToken ct = default);
}
