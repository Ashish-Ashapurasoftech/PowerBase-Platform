using System.Data;
using System.Text;
using Dapper;
using PowerBase.Application.Auth;
using PowerBase.Application.AuditLogs;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
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

    private const string InsertActivityLogSql = """
        INSERT INTO audit.ActivityLog (TenantId, UserId, Action, EntityType, EntityId, AppId, OldValues, NewValues, IpAddress, OccurredOn)
        VALUES (@tenantId, @userId, @action, @entityType, @entityId, @appId, @oldValues, @newValues, @ipAddress, SYSUTCDATETIME())
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

    public async Task LogActivityAsync(
        string action,
        string entityType,
        string entityId,
        long? appId = null,
        string? oldValues = null,
        string? newValues = null,
        CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(InsertActivityLogSql, new
        {
            tenantId = QueryContext.TenantId,
            userId = QueryContext.UserId,
            action,
            entityType,
            entityId,
            appId,
            oldValues,
            newValues,
            ipAddress = QueryContext.IpAddress,
        }, cancellationToken: ct));
    }

    public async Task<(IReadOnlyList<ActivityLog> Items, int Total)> QueryActivityLogsAsync(
        ActivityLogFilter filter,
        CancellationToken ct = default)
    {
        var (where, parameters) = BuildWhereClause(filter);
        var offset = (filter.Page - 1) * filter.PageSize;

        var countSql = $"""
            SELECT COUNT(*)
            FROM audit.ActivityLog a
            LEFT JOIN core.[User] u ON u.Id = a.UserId
            {where}
            """;

        var dataSql = $"""
            SELECT a.Id, a.TenantId, a.UserId, u.Email AS UserEmail, u.Name AS UserName,
                   a.Action, a.EntityType, a.EntityId, a.AppId, a.OldValues, a.NewValues, a.IpAddress, a.UserAgent, a.OccurredOn
            FROM audit.ActivityLog a
            LEFT JOIN core.[User] u ON u.Id = a.UserId
            {where}
            ORDER BY a.OccurredOn DESC
            OFFSET {offset} ROWS FETCH NEXT {filter.PageSize} ROWS ONLY
            """;

        await using var connection = ConnectionFactory.Create();
        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(countSql, parameters, cancellationToken: ct));
        var items = (await connection.QueryAsync<ActivityLog>(new CommandDefinition(dataSql, parameters, cancellationToken: ct))).AsList();

        return (items, total);
    }

    public async Task<IReadOnlyList<ActivityLog>> ExportActivityLogsAsync(
        ActivityLogFilter filter,
        CancellationToken ct = default)
    {
        var (where, parameters) = BuildWhereClause(filter);

        var sql = $"""
            SELECT a.Id, a.TenantId, a.UserId, u.Email AS UserEmail, u.Name AS UserName,
                   a.Action, a.EntityType, a.EntityId, a.AppId, a.OldValues, a.NewValues, a.IpAddress, a.UserAgent, a.OccurredOn
            FROM audit.ActivityLog a
            LEFT JOIN core.[User] u ON u.Id = a.UserId
            {where}
            ORDER BY a.OccurredOn DESC
            """;

        await using var connection = ConnectionFactory.Create();
        return (await connection.QueryAsync<ActivityLog>(new CommandDefinition(sql, parameters, cancellationToken: ct))).AsList();
    }

    private (string Clause, object Parameters) BuildWhereClause(ActivityLogFilter filter)
    {
        var conditions = new List<string> { "a.TenantId = @tenantId" };
        if (filter.From.HasValue) conditions.Add("a.OccurredOn >= @from");
        if (filter.To.HasValue) conditions.Add("a.OccurredOn <= @to");
        if (filter.AppId.HasValue) conditions.Add("a.AppId = @appId");
        if (!string.IsNullOrWhiteSpace(filter.Email)) conditions.Add("u.Email = @email");
        if (!string.IsNullOrWhiteSpace(filter.EntityType)) conditions.Add("a.EntityType = @entityType");
        if (!string.IsNullOrWhiteSpace(filter.Action)) conditions.Add("a.Action = @action");

        var clause = "WHERE " + string.Join(" AND ", conditions);

        var parameters = new
        {
            tenantId = QueryContext.TenantId,
            from = filter.From,
            to = filter.To,
            appId = filter.AppId,
            email = filter.Email,
            entityType = filter.EntityType,
            action = filter.Action,
        };

        return (clause, parameters);
    }
}
