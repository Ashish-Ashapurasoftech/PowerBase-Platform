namespace PowerBase.Application.Common.Interfaces;

public interface IAuditRepository
{
    Task RecordLoginAttemptAsync(string email, string ipAddress, bool isSuccess, CancellationToken ct = default);
    Task CreateSessionAsync(long userId, long tenantId, string jwtId, string ipAddress, DateTime expiresAt, CancellationToken ct = default);
}
