using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class AuditRepository : BaseRepository, IAuditRepository
{
    private const string InsertLoginAttemptSql = """
        INSERT INTO audit.LoginAttempt (Email, IpAddress, IsSuccess, AttemptedAt)
        VALUES (@email, @ipAddress, @isSuccess, SYSUTCDATETIME())
        """;

    private const string InsertSessionSql = """
        INSERT INTO audit.UserSession (UserId, TenantId, JwtId, IpAddress, IsRevoked, ExpiresAt, CreatedAt)
        VALUES (@userId, @tenantId, @jwtId, @ipAddress, 0, @expiresAt, SYSUTCDATETIME())
        """;

    public AuditRepository(DbConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    public async Task RecordLoginAttemptAsync(string email, string ipAddress, bool isSuccess, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        await connection.ExecuteAsync(
            new CommandDefinition(InsertLoginAttemptSql, new { email, ipAddress, isSuccess }, cancellationToken: ct));
    }

    public async Task CreateSessionAsync(long userId, long tenantId, string jwtId, string ipAddress, DateTime expiresAt, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        await connection.ExecuteAsync(
            new CommandDefinition(InsertSessionSql, new { userId, tenantId, jwtId, ipAddress, expiresAt }, cancellationToken: ct));
    }
}
