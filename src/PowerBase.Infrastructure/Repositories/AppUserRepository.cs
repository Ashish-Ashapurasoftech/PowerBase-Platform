using System.Data;
using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class AppUserRepository : BaseRepository, IAppUserRepository
{
    private const string ListByAppIdSql = """
        SELECT
            au.PublicId,
            u.PublicId  AS UserPublicId,
            u.Name      AS UserName,
            u.Email     AS UserEmail,
            ar.PublicId AS RolePublicId,
            ar.Name     AS RoleName,
            au.Status,
            au.CreatedOn
        FROM meta.AppUser au
        JOIN core.[User]  u  ON u.Id  = au.UserId
        JOIN meta.AppRole ar ON ar.Id = au.AppRoleId
        WHERE au.AppId    = @appId
          AND au.TenantId = @tenantId
          AND au.IsDeleted = 0
        ORDER BY u.Name
        """;

    private const string GetByAppAndUserSql = """
        SELECT Id, PublicId, AppId, TenantId, UserId, AppRoleId, Status, AddedBy, CreatedOn, UpdatedOn, IsDeleted
        FROM meta.AppUser
        WHERE AppId = @appId AND UserId = @userId AND TenantId = @tenantId AND IsDeleted = 0
        """;

    private const string InsertSql = """
        INSERT INTO meta.AppUser (AppId, TenantId, UserId, AppRoleId, Status, AddedBy, CreatedOn)
        VALUES (@appId, @tenantId, @userId, @appRoleId, @status, @addedBy, SYSUTCDATETIME())
        """;

    private const string UpdateRoleSql = """
        UPDATE meta.AppUser
        SET AppRoleId = @appRoleId, UpdatedOn = SYSUTCDATETIME()
        WHERE AppId = @appId AND UserId = @userId AND TenantId = @tenantId AND IsDeleted = 0
        """;

    private const string GetUserRoleNameSql = """
        SELECT ar.Name
        FROM meta.AppUser au
        JOIN meta.AppRole ar ON ar.Id = au.AppRoleId
        WHERE au.AppId = @appId AND au.UserId = @userId AND au.TenantId = @tenantId AND au.IsDeleted = 0
        """;

    private const string RemoveSql = """
        UPDATE meta.AppUser
        SET IsDeleted = 1, UpdatedOn = SYSUTCDATETIME()
        WHERE AppId = @appId AND UserId = @userId AND TenantId = @tenantId AND IsDeleted = 0
        """;

    public AppUserRepository(DbConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    public async Task<IReadOnlyList<AppUserDetail>> ListByAppIdAsync(long appId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var results = await connection.QueryAsync<AppUserDetail>(
            new CommandDefinition(ListByAppIdSql, new { appId, tenantId = QueryContext.TenantId }, cancellationToken: ct));
        return results.AsList();
    }

    public async Task<AppUser?> GetByAppAndUserAsync(long appId, long userId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<AppUser>(
            new CommandDefinition(GetByAppAndUserSql, new { appId, userId, tenantId = QueryContext.TenantId }, cancellationToken: ct));
    }

    public async Task CreateAsync(AppUser appUser, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        var parameters = new
        {
            appId = appUser.AppId,
            tenantId = QueryContext.TenantId,
            userId = appUser.UserId,
            appRoleId = appUser.AppRoleId,
            status = appUser.Status,
            addedBy = QueryContext.UserId,
        };

        if (transaction is not null)
        {
            await transaction.Connection!.ExecuteAsync(
                new CommandDefinition(InsertSql, parameters, transaction, cancellationToken: ct));
            return;
        }

        await using var connection = ConnectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(InsertSql, parameters, cancellationToken: ct));
    }

    public async Task UpdateRoleAsync(long appId, long userId, long newRoleId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        await connection.ExecuteAsync(
            new CommandDefinition(UpdateRoleSql, new { appId, userId, appRoleId = newRoleId, tenantId = QueryContext.TenantId }, cancellationToken: ct));
    }

    public async Task<string?> GetUserRoleNameAsync(long appId, long userId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.ExecuteScalarAsync<string?>(
            new CommandDefinition(GetUserRoleNameSql, new { appId, userId, tenantId = QueryContext.TenantId }, cancellationToken: ct));
    }

    public async Task RemoveAsync(long appId, long userId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        await connection.ExecuteAsync(
            new CommandDefinition(RemoveSql, new { appId, userId, tenantId = QueryContext.TenantId }, cancellationToken: ct));
    }
}
