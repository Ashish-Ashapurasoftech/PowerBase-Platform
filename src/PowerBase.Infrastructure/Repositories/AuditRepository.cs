using System.Data;
using System.Text;
using Dapper;
using PowerBase.Application.Auth;
using PowerBase.Application.AuditLogs;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

// Auth-audit methods (LoginAttempt, UserSession, InviteToken) use the control
// connection.  Per-tenant activity (ActivityLog) uses the tenant connection.
public class AuditRepository : IAuditRepository
{
    private readonly IControlConnectionFactory _controlFactory;
    private readonly ITenantConnectionFactory _tenantFactory;
    private readonly IQueryContext _queryContext;

    private const string InsertLoginAttemptSql = """
        INSERT INTO audit.LoginAttempt (EmailAttempted, IpAddress, WasSuccessful, UserId, FailureReason, AttemptedOn)
        VALUES (@emailAttempted, @ipAddress, @wasSuccessful, @userId, @failureReason, SYSUTCDATETIME())
        """;

    private const string InsertSessionSql = """
        INSERT INTO audit.UserSession (UserId, TenantId, JwtId, IpAddress, ExpiresOn, IssuedOn)
        VALUES (@userId, @tenantId, @jwtId, @ipAddress, @expiresOn, SYSUTCDATETIME())
        """;

    private const string InsertInviteTokenSql = """
        INSERT INTO audit.InviteToken (UserId, TenantId, TenantRoleId, AppId, AppRoleId, TokenHash, InvitedBy, ExpiresOn, CreatedOn)
        VALUES (@userId, @tenantId, @tenantRoleId, @appId, @appRoleId, @tokenHash, @invitedBy, @expiresOn, SYSUTCDATETIME())
        """;

    private const string GetInviteTokenByHashSql = """
        SELECT Id, UserId, TenantId, TenantRoleId, AppId, AppRoleId, TokenHash, InvitedBy, ExpiresOn, UsedOn, CreatedOn
        FROM audit.InviteToken
        WHERE TokenHash = @tokenHash
        """;

    private const string ConsumeInviteTokenSql = """
        UPDATE audit.InviteToken SET UsedOn = SYSUTCDATETIME() WHERE Id = @id
        """;

    private const string InsertActivityLogSql = """
        INSERT INTO audit.ActivityLog (UserId, UserName, UserEmail, Action, EntityType, EntityId, EntityTitle, AppId, OldValues, NewValues, IpAddress, OccurredOn)
        VALUES (@userId, @userName, @userEmail, @action, @entityType, @entityId, @entityTitle, @appId, @oldValues, @newValues, @ipAddress, SYSUTCDATETIME())
        """;

    public AuditRepository(
        IControlConnectionFactory controlFactory,
        ITenantConnectionFactory tenantFactory,
        IQueryContext queryContext)
    {
        _controlFactory = controlFactory;
        _tenantFactory = tenantFactory;
        _queryContext = queryContext;
    }

    // ── control-scoped: auth audit ───────────────────────────────────────────

    public async Task RecordLoginAttemptAsync(string emailAttempted, string ipAddress, bool wasSuccessful, long? userId = null, string? failureReason = null, CancellationToken ct = default)
    {
        await using var connection = _controlFactory.Create();
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
        await using var connection = _controlFactory.Create();
        await connection.ExecuteAsync(
            new CommandDefinition(InsertSessionSql, new { userId, tenantId, jwtId, ipAddress, expiresOn }, cancellationToken: ct));
    }

    public async Task CreateInviteTokenAsync(long userId, long? tenantId, long? tenantRoleId, string tokenHash, DateTime expiresOn, long invitedBy, long? appId = null, long? appRoleId = null, CancellationToken ct = default)
    {
        await using var connection = _controlFactory.Create();
        await connection.ExecuteAsync(
            new CommandDefinition(InsertInviteTokenSql, new { userId, tenantId, tenantRoleId, appId, appRoleId, tokenHash, invitedBy, expiresOn }, cancellationToken: ct));
    }

    public async Task<InviteTokenRecord?> GetInviteTokenByHashAsync(string tokenHash, CancellationToken ct = default)
    {
        await using var connection = _controlFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<InviteTokenRecord>(
            new CommandDefinition(GetInviteTokenByHashSql, new { tokenHash }, cancellationToken: ct));
    }

    public async Task ConsumeInviteTokenAsync(long tokenId, CancellationToken ct = default)
    {
        await using var connection = _controlFactory.Create();
        await connection.ExecuteAsync(
            new CommandDefinition(ConsumeInviteTokenSql, new { id = tokenId }, cancellationToken: ct));
    }

    // ── tenant-scoped: activity log ──────────────────────────────────────────

    public async Task LogActivityAsync(
        string action,
        string entityType,
        string entityId,
        string? entityTitle = null,
        long? appId = null,
        string? oldValues = null,
        string? newValues = null,
        CancellationToken ct = default)
    {
        await using var connection = await _tenantFactory.CreateAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(InsertActivityLogSql, new
        {
            userId    = _queryContext.UserId,
            userName  = _queryContext.UserName,
            userEmail = _queryContext.UserEmail,
            action, entityType, entityId, entityTitle, appId, oldValues, newValues,
            ipAddress = _queryContext.IpAddress,
        }, cancellationToken: ct));
    }

    public async Task<(IReadOnlyList<ActivityLog> Items, int Total)> QueryActivityLogsAsync(
        ActivityLogFilter filter, CancellationToken ct = default)
    {
        var (where, parameters) = BuildWhereClause(filter);
        var offset = (filter.Page - 1) * filter.PageSize;

        var countSql = $"""
            SELECT COUNT(*)
            FROM audit.ActivityLog a
            {where}
            """;

        var dataSql = $"""
            SELECT a.Id, a.UserId, a.UserName, a.UserEmail,
                   a.Action, a.EntityType, a.EntityId, a.EntityTitle, a.AppId, a.OldValues, a.NewValues, a.IpAddress, a.UserAgent, a.OccurredOn
            FROM audit.ActivityLog a
            {where}
            ORDER BY a.OccurredOn DESC
            OFFSET {offset} ROWS FETCH NEXT {filter.PageSize} ROWS ONLY
            """;

        await using var connection = await _tenantFactory.CreateAsync(ct);
        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(countSql, parameters, cancellationToken: ct));
        var items = (await connection.QueryAsync<ActivityLog>(new CommandDefinition(dataSql, parameters, cancellationToken: ct))).AsList();
        return (items, total);
    }

    public async Task<IReadOnlyList<ActivityLog>> ExportActivityLogsAsync(
        ActivityLogFilter filter, CancellationToken ct = default)
    {
        var (where, parameters) = BuildWhereClause(filter);

        var sql = $"""
            SELECT a.Id, a.UserId, a.UserName, a.UserEmail,
                   a.Action, a.EntityType, a.EntityId, a.EntityTitle, a.AppId, a.OldValues, a.NewValues, a.IpAddress, a.UserAgent, a.OccurredOn
            FROM audit.ActivityLog a
            {where}
            ORDER BY a.OccurredOn DESC
            """;

        await using var connection = await _tenantFactory.CreateAsync(ct);
        return (await connection.QueryAsync<ActivityLog>(new CommandDefinition(sql, parameters, cancellationToken: ct))).AsList();
    }

    private static (string Clause, object Parameters) BuildWhereClause(ActivityLogFilter filter)
    {
        var conditions = new List<string> { "1=1" };
        if (filter.From.HasValue)                          conditions.Add("a.OccurredOn >= @from");
        if (filter.To.HasValue)                            conditions.Add("a.OccurredOn <= @to");
        if (filter.AppId.HasValue)                         conditions.Add("a.AppId = @appId");
        if (!string.IsNullOrWhiteSpace(filter.Email))      conditions.Add("a.UserEmail = @email");
        if (!string.IsNullOrWhiteSpace(filter.EntityType)) conditions.Add("a.EntityType = @entityType");
        if (!string.IsNullOrWhiteSpace(filter.EntityId)) conditions.Add("a.EntityId = @entityId");
        if (!string.IsNullOrWhiteSpace(filter.Action)) conditions.Add("a.Action = @action");

        var clause = "WHERE " + string.Join(" AND ", conditions);
        var parameters = new
        {
            from       = filter.From,
            to         = filter.To,
            appId      = filter.AppId,
            email      = filter.Email,
            entityType = filter.EntityType,
            entityId = filter.EntityId,
            action = filter.Action,
        };
        return (clause, parameters);
    }
}
