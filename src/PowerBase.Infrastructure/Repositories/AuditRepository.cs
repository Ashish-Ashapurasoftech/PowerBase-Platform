using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class AuditRepository : BaseRepository, IAuditRepository
{
    private const string InsertLoginAttemptSql = """
        INSERT INTO audit.LoginAttempt (EmailAttempted, IpAddress, WasSuccessful, UserId, FailureReason, AttemptedOn)
        VALUES (@emailAttempted, @ipAddress, @wasSuccessful, @userId, @failureReason, SYSUTCDATETIME())
        """;

    private const string InsertSessionSql = """
        INSERT INTO audit.UserSession (UserId, TenantId, JwtId, IpAddress, ExpiresOn, CreatedOn)
        VALUES (@userId, @tenantId, @jwtId, @ipAddress, @expiresOn, SYSUTCDATETIME())
        """;

    public AuditRepository(DbConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    public async Task RecordLoginAttemptAsync(string emailAttempted, string ipAddress, bool wasSuccessful, long? userId = null, string? failureReason = null, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        await connection.ExecuteAsync(
            new CommandDefinition(InsertLoginAttemptSql, new { emailAttempted, ipAddress, wasSuccessful, userId, failureReason }, cancellationToken: ct));
    }

    public async Task CreateSessionAsync(long userId, long tenantId, Guid jwtId, string ipAddress, DateTime expiresOn, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        await connection.ExecuteAsync(
            new CommandDefinition(InsertSessionSql, new { userId, tenantId, jwtId, ipAddress, expiresOn }, cancellationToken: ct));
    }
}
