using System.Data;
using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class AppUserRepository : TenantRepositoryBase, IAppUserRepository
{
    private const string ListByAppIdSql = """
        SELECT
            au.PublicId,
            au.UserPublicId,
            au.UserName,
            au.UserEmail,
            ar.PublicId AS RolePublicId,
            ar.Name     AS RoleName,
            au.Status,
            au.CreatedOn
        FROM meta.AppUser au
        JOIN meta.AppRole ar ON ar.Id = au.AppRoleId
        WHERE au.AppId    = @appId
          AND au.IsDeleted = 0
        ORDER BY au.UserName
        """;

    private const string GetByAppAndUserSql = """
        SELECT Id, PublicId, AppId, UserId, UserPublicId, AppRoleId, Status, AddedBy, CreatedOn, UpdatedOn, IsDeleted
        FROM meta.AppUser
        WHERE AppId = @appId AND UserId = @userId AND IsDeleted = 0
        """;

    private const string InsertSql = """
        INSERT INTO meta.AppUser (AppId, UserId, UserPublicId, UserName, UserEmail, AppRoleId, Status, AddedBy, CreatedOn)
        VALUES (@appId, @userId, @userPublicId, @userName, @userEmail, @appRoleId, @status, @addedBy, SYSUTCDATETIME())
        """;

    private const string UpdateRoleSql = """
        UPDATE meta.AppUser
        SET AppRoleId = @appRoleId, UpdatedOn = SYSUTCDATETIME()
        WHERE AppId = @appId AND UserId = @userId AND IsDeleted = 0
        """;

    private const string GetUserRoleNameSql = """
        SELECT ar.Name
        FROM meta.AppUser au
        JOIN meta.AppRole ar ON ar.Id = au.AppRoleId
        WHERE au.AppId = @appId AND au.UserId = @userId AND au.IsDeleted = 0
        """;

    private const string GetUserRolePublicIdSql = """
        SELECT ar.PublicId
        FROM meta.AppUser au
        JOIN meta.AppRole ar ON ar.Id = au.AppRoleId
        WHERE au.AppId = @appId AND au.UserId = @userId AND au.IsDeleted = 0
        """;

    private const string GetPermissionFlagsSql = """
        SELECT ar.CanViewRecords, ar.CanAddRecords, ar.CanEditRecords, ar.CanDeleteRecords
        FROM meta.AppUser au
        JOIN meta.AppRole ar ON ar.Id = au.AppRoleId
        JOIN meta.AppRolePermission arp ON arp.AppRoleId = ar.Id
        JOIN meta.Permission p ON p.Id = arp.PermissionId
        WHERE au.AppId = @appId AND au.UserId = @userId AND au.IsDeleted = 0
        """;

    private const string GetAppPermissionsSql = """
        SELECT p.Code
        FROM meta.AppUser au
        JOIN meta.AppRole ar ON ar.Id = au.AppRoleId
        JOIN meta.AppRolePermission arp ON arp.AppRoleId = ar.Id
        JOIN meta.Permission p ON p.Id = arp.PermissionId
        WHERE au.AppId = @appId AND au.UserId = @userId AND au.IsDeleted = 0
        """;

    private const string RemoveSql = """
        UPDATE meta.AppUser
        SET IsDeleted = 1, UpdatedOn = SYSUTCDATETIME()
        WHERE AppId = @appId AND UserId = @userId AND IsDeleted = 0
        """;

    public AppUserRepository(ITenantConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    public async Task<IReadOnlyList<AppUserDetail>> ListByAppIdAsync(long appId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var results = await connection.QueryAsync<AppUserDetail>(
            new CommandDefinition(ListByAppIdSql, new { appId }, cancellationToken: ct));
        return results.AsList();
    }

    public async Task<AppUser?> GetByAppAndUserAsync(long appId, long userId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<AppUser>(
            new CommandDefinition(GetByAppAndUserSql, new { appId, userId }, cancellationToken: ct));
    }

    public async Task CreateAsync(AppUser appUser, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        var parameters = new
        {
            appId      = appUser.AppId,
            userId     = appUser.UserId,
            userPublicId = appUser.UserPublicId,
            userName   = appUser.UserName,
            userEmail  = appUser.UserEmail,
            appRoleId  = appUser.AppRoleId,
            status     = appUser.Status,
            addedBy    = QueryContext.UserId,
        };

        if (transaction is not null)
        {
            await transaction.Connection!.ExecuteAsync(
                new CommandDefinition(InsertSql, parameters, transaction, cancellationToken: ct));
            return;
        }

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(InsertSql, parameters, cancellationToken: ct));
    }

    public async Task UpdateRoleAsync(long appId, long userId, long newRoleId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition(UpdateRoleSql, new { appId, userId, appRoleId = newRoleId }, cancellationToken: ct));
    }

    public async Task<string?> GetUserRoleNameAsync(long appId, long userId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<string?>(
            new CommandDefinition(GetUserRoleNameSql, new { appId, userId }, cancellationToken: ct));
    }

    public async Task<Guid?> GetUserRolePublicIdAsync(long appId, long userId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<Guid?>(
            new CommandDefinition(GetUserRolePublicIdSql, new { appId, userId }, cancellationToken: ct));
    }

    public async Task<IReadOnlySet<string>> GetUserAppPermissionsAsync(long appId, long userId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var result = await connection.QueryAsync<string>(
            new CommandDefinition(GetAppPermissionsSql, new { appId, userId }, cancellationToken: ct));
        return result.ToHashSet();
    }

    public async Task RemoveAsync(long appId, long userId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition(RemoveSql, new { appId, userId }, cancellationToken: ct));
    }
}
