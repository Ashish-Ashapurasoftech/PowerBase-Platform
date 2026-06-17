using System.Data;
using PowerBase.Application.Auth;
using PowerBase.Application.AuditLogs;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IAuditRepository
{
    Task RecordLoginAttemptAsync(string emailAttempted, string ipAddress, bool wasSuccessful, long? userId = null, string? failureReason = null, CancellationToken ct = default);
    Task CreateSessionAsync(long userId, long tenantId, Guid jwtId, string ipAddress, DateTime expiresOn, IDbTransaction? transaction = null, CancellationToken ct = default);

    Task CreateInviteTokenAsync(long userId, long? tenantId, long? tenantRoleId, string tokenHash, DateTime expiresOn, long invitedBy, long? appId = null, long? appRoleId = null, CancellationToken ct = default);
    Task<InviteTokenRecord?> GetInviteTokenByHashAsync(string tokenHash, CancellationToken ct = default);
    Task ConsumeInviteTokenAsync(long tokenId, CancellationToken ct = default);

    Task LogActivityAsync(
        string action,
        string entityType,
        string entityId,
        string? entityTitle = null,
        long? appId = null,
        string? oldValues = null,
        string? newValues = null,
        CancellationToken ct = default);

    Task<(IReadOnlyList<ActivityLog> Items, int Total)> QueryActivityLogsAsync(
        ActivityLogFilter filter,
        CancellationToken ct = default);

    Task<IReadOnlyList<ActivityLog>> ExportActivityLogsAsync(
        ActivityLogFilter filter,
        CancellationToken ct = default);
}
