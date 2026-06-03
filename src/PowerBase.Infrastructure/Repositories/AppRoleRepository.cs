using System.Data;
using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class AppRoleRepository : TenantRepositoryBase, IAppRoleRepository
{
    private const string ListDetailsByAppIdSql = """
        SELECT r.Id, r.PublicId, r.AppId, r.Name, r.IsDefault, r.IsSystem,
               ISNULL(STRING_AGG(p.Code, ','), '') AS PermissionsString
        FROM meta.AppRole r
        LEFT JOIN meta.AppRolePermission arp ON arp.AppRoleId = r.Id
        LEFT JOIN meta.Permission p ON p.Id = arp.PermissionId
        WHERE r.AppId = @appId AND r.IsDeleted = 0
        GROUP BY r.Id, r.PublicId, r.AppId, r.Name, r.IsDefault, r.IsSystem
        ORDER BY r.IsSystem DESC, r.Name
        """;

    private const string DeleteRolePermissionsSql = """
        DELETE FROM meta.AppRolePermission WHERE AppRoleId = @appRoleId
        """;

    private const string InsertRolePermissionsSql = """
        INSERT INTO meta.AppRolePermission (AppRoleId, PermissionId)
        SELECT @appRoleId, Id FROM meta.Permission WHERE Code IN @codes
        """;

    private const string SoftDeleteSql = """
        UPDATE meta.AppRole
        SET IsDeleted = 1
        WHERE PublicId = @publicId AND IsSystem = 0 AND IsDeleted = 0
        """;

    private const string GetByPublicIdSql = """
        SELECT Id, PublicId, AppId, Name, IsDefault, IsSystem
        FROM meta.AppRole
        WHERE PublicId = @publicId AND IsDeleted = 0
        """;

    private const string NameExistsSql = """
        SELECT CASE WHEN EXISTS (
            SELECT 1 FROM meta.AppRole
            WHERE AppId = @appId AND Name = @name AND IsDeleted = 0
        ) THEN 1 ELSE 0 END
        """;

    private const string InsertSql = """
        INSERT INTO meta.AppRole (AppId, Name, IsDefault, IsSystem, CreatedBy)
        OUTPUT inserted.Id, inserted.PublicId
        VALUES (@appId, @name, @isDefault, @isSystem, @createdBy)
        """;

    public AppRoleRepository(ITenantConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    public async Task<IReadOnlyList<AppRoleDetail>> ListDetailsByAppIdAsync(long appId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var results = await connection.QueryAsync(
            new CommandDefinition(ListDetailsByAppIdSql, new { appId }, cancellationToken: ct));

        return results.Select(r => new AppRoleDetail(
            (long)r.Id, (Guid)r.PublicId, (long)r.AppId, (string)r.Name, (bool)r.IsDefault, (bool)r.IsSystem,
            ((string)r.PermissionsString).Split(',', StringSplitOptions.RemoveEmptyEntries).ToList()
        )).ToList();
    }

    public async Task<AppRole?> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<AppRole>(
            new CommandDefinition(GetByPublicIdSql, new { publicId }, cancellationToken: ct));
    }

    public async Task<bool> NameExistsInAppAsync(long appId, string name, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(NameExistsSql, new { appId, name }, cancellationToken: ct));
    }

    public async Task<(long Id, Guid PublicId)> CreateAsync(AppRole role, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        var parameters = new { appId = role.AppId, name = role.Name, isDefault = role.IsDefault, isSystem = role.IsSystem, createdBy = QueryContext.UserId };

        if (transaction is not null)
            return await transaction.Connection!.QuerySingleAsync<(long Id, Guid PublicId)>(
                new CommandDefinition(InsertSql, parameters, transaction, cancellationToken: ct));

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.QuerySingleAsync<(long Id, Guid PublicId)>(
            new CommandDefinition(InsertSql, parameters, cancellationToken: ct));
    }

    public async Task SetPermissionsAsync(long appRoleId, IReadOnlyList<string> permissionCodes, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        if (transaction != null)
        {
            await transaction.Connection!.ExecuteAsync(new CommandDefinition(DeleteRolePermissionsSql, new { appRoleId }, transaction, cancellationToken: ct));
            if (permissionCodes.Any())
                await transaction.Connection!.ExecuteAsync(new CommandDefinition(InsertRolePermissionsSql, new { appRoleId, codes = permissionCodes }, transaction, cancellationToken: ct));
        }
        else
        {
            await using var conn = await ConnectionFactory.CreateAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(DeleteRolePermissionsSql, new { appRoleId }, cancellationToken: ct));
            if (permissionCodes.Any())
                await conn.ExecuteAsync(new CommandDefinition(InsertRolePermissionsSql, new { appRoleId, codes = permissionCodes }, cancellationToken: ct));
        }
    }

    public async Task DeleteAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition(SoftDeleteSql, new { publicId }, cancellationToken: ct));
    }
}
