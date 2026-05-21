using System.Data;
using Dapper;
using PowerBase.Application.Auth;
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
        INSERT INTO audit.UserSession (UserId, TenantId, JwtId, IpAddress, ExpiresOn, IssuedOn)
        VALUES (@userId, @tenantId, @jwtId, @ipAddress, @expiresOn, SYSUTCDATETIME())
        """;

    private const string InsertInviteTokenSql = """
        INSERT INTO audit.InviteToken (UserId, TenantId, TenantRoleId, TokenHash, InvitedBy, ExpiresOn, CreatedOn)
        VALUES (@userId, @tenantId, @tenantRoleId, @tokenHash, @invitedBy, @expiresOn, SYSUTCDATETIME())
        """;

    private const string GetInviteTokenByHashSql = """
        SELECT Id, UserId, TenantId, TenantRoleId, TokenHash, InvitedBy, ExpiresOn, UsedOn, CreatedOn
        FROM audit.InviteToken
        WHERE TokenHash = @tokenHash
        """;

    private const string ConsumeInviteTokenSql = """
        UPDATE audit.InviteToken SET UsedOn = SYSUTCDATETIME() WHERE Id = @id
        """;

    public AuditRepository(DbConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    public async Task RecordLoginAttemptAsync(string emailAttempted, string ipAddress, bool wasSuccessful, long? userId = null, string? failureReason = null, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        await connection.ExecuteAsync(
            new CommandDefinition(InsertLoginAttemptSql, new { emailAttempted, ipAddress, wasSuccessful, userId, failureReason }, cancellationToken: ct));
    }

    public async Task CreateSessionAsync(long userId, long tenantId, Guid jwtId, string ipAddress, DateTime expiresOn, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        if (transaction is not null)
        {
            await transaction.Connection!.ExecuteAsync(
                new CommandDefinition(InsertSessionSql, new { userId, tenantId, jwtId, ipAddress, expiresOn }, transaction, cancellationToken: ct));
            return;
        }
        await using var connection = ConnectionFactory.Create();
        await connection.ExecuteAsync(
            new CommandDefinition(InsertSessionSql, new { userId, tenantId, jwtId, ipAddress, expiresOn }, cancellationToken: ct));
    }

    public async Task CreateInviteTokenAsync(long userId, long tenantId, long tenantRoleId, string tokenHash, DateTime expiresOn, long invitedBy, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        await connection.ExecuteAsync(
            new CommandDefinition(InsertInviteTokenSql, new { userId, tenantId, tenantRoleId, tokenHash, invitedBy, expiresOn }, cancellationToken: ct));
    }

    public async Task<InviteTokenRecord?> GetInviteTokenByHashAsync(string tokenHash, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<InviteTokenRecord>(
            new CommandDefinition(GetInviteTokenByHashSql, new { tokenHash }, cancellationToken: ct));
    }

    public async Task ConsumeInviteTokenAsync(long tokenId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        await connection.ExecuteAsync(
            new CommandDefinition(ConsumeInviteTokenSql, new { id = tokenId }, cancellationToken: ct));
    }
}
