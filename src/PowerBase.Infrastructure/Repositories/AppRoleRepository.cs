using System.Data;
using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class AppRoleRepository : TenantRepositoryBase, IAppRoleRepository
{
    private const string ListDetailsByAppIdSql = """
        SELECT r.Id, r.PublicId, r.AppId, r.Name, r.IsDefault, r.IsSystem, r.ManageableRolesType, r.Rank,
               ISNULL(STRING_AGG(p.Code, ','), '') AS PermissionsString
        FROM meta.AppRole r
        LEFT JOIN meta.AppRolePermission arp ON arp.AppRoleId = r.Id
        LEFT JOIN meta.Permission p ON p.Id = arp.PermissionId
        WHERE r.AppId = @appId AND r.IsDeleted = 0
        GROUP BY r.Id, r.PublicId, r.AppId, r.Name, r.IsDefault, r.IsSystem, r.ManageableRolesType, r.Rank
        ORDER BY ISNULL(r.Rank, 999999) ASC, r.Name
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
        SELECT Id, PublicId, AppId, Name, IsDefault, IsSystem, ManageableRolesType, Rank
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
        INSERT INTO meta.AppRole (AppId, Name, IsDefault, IsSystem, CreatedBy, ManageableRolesType, Rank)
        OUTPUT inserted.Id, inserted.PublicId
        VALUES (@appId, @name, @isDefault, @isSystem, @createdBy, @manageableRolesType, @rank)
        """;

    public AppRoleRepository(ITenantConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    public async Task<IReadOnlyList<AppRoleDetail>> ListDetailsByAppIdAsync(long appId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var results = await connection.QueryAsync(
            new CommandDefinition(ListDetailsByAppIdSql, new { appId }, cancellationToken: ct));

        // Fetch manageable roles mappings for this app
        const string manageableSql = """
            SELECT mp.AppRoleId, r.PublicId AS AllowedPublicId
            FROM meta.AppRoleManageableRole mp
            JOIN meta.AppRole r ON r.Id = mp.ManageableRoleId
            JOIN meta.AppRole parent ON parent.Id = mp.AppRoleId
            WHERE parent.AppId = @appId AND r.IsDeleted = 0
            """;
        var manageableResults = await connection.QueryAsync(
            new CommandDefinition(manageableSql, new { appId }, cancellationToken: ct));

        var manageableMap = manageableResults
            .GroupBy(x => (long)x.AppRoleId)
            .ToDictionary(g => g.Key, g => g.Select(x => (Guid)x.AllowedPublicId).ToList());

        return results.Select(r => {
            long id = (long)r.Id;
            var manageableList = manageableMap.TryGetValue(id, out var list) ? list : new List<Guid>();
            return new AppRoleDetail(
                id, 
                (Guid)r.PublicId, 
                (long)r.AppId, 
                (string)r.Name, 
                (bool)r.IsDefault, 
                (bool)r.IsSystem,
                ((string)r.PermissionsString).Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
                (string)(r.ManageableRolesType ?? "None"),
                r.Rank == null ? (int?)null : (int)r.Rank,
                manageableList
            );
        }).ToList();
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
        var parameters = new { 
            appId = role.AppId, 
            name = role.Name, 
            isDefault = role.IsDefault, 
            isSystem = role.IsSystem, 
            createdBy = QueryContext.UserId,
            manageableRolesType = role.ManageableRolesType ?? "None",
            rank = role.Rank
        };

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

    public async Task<IReadOnlyList<Guid>> GetManageableRolePublicIdsAsync(long roleId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT r.PublicId
            FROM meta.AppRoleManageableRole mp
            JOIN meta.AppRole r ON r.Id = mp.ManageableRoleId
            WHERE mp.AppRoleId = @roleId AND r.IsDeleted = 0
            """;
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var results = await connection.QueryAsync<Guid>(new CommandDefinition(sql, new { roleId }, cancellationToken: ct));
        return results.ToList();
    }

    public async Task UpdateRoleHierarchyAsync(Guid publicId, string manageableRolesType, int? rank, IReadOnlyList<Guid> manageableRolePublicIds, CancellationToken ct = default)
    {
        const string getRoleSql = "SELECT Id, AppId FROM meta.AppRole WHERE PublicId = @publicId AND IsDeleted = 0";
        const string updateRoleSql = """
            UPDATE meta.AppRole
            SET ManageableRolesType = @manageableRolesType, Rank = @rank
            WHERE Id = @id
            """;
        const string clearManageableSql = "DELETE FROM meta.AppRoleManageableRole WHERE AppRoleId = @roleId";
        const string insertManageableSql = """
            INSERT INTO meta.AppRoleManageableRole (AppRoleId, ManageableRoleId)
            SELECT @roleId, Id FROM meta.AppRole WHERE PublicId = @allowedPublicId AND AppId = @appId AND IsDeleted = 0
            """;

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        if (connection.State != ConnectionState.Open) connection.Open();
        
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            var roleInfo = await connection.QuerySingleOrDefaultAsync(
                new CommandDefinition(getRoleSql, new { publicId }, transaction, cancellationToken: ct));
            
            if (roleInfo == null)
                throw new NotFoundException("AppRole", publicId);

            long roleId = (long)roleInfo.Id;
            long appId = (long)roleInfo.AppId;

            // 1. Update AppRole fields
            await connection.ExecuteAsync(
                new CommandDefinition(updateRoleSql, new { id = roleId, manageableRolesType, rank }, transaction, cancellationToken: ct));

            // 2. Clear manageable list
            await connection.ExecuteAsync(
                new CommandDefinition(clearManageableSql, new { roleId }, transaction, cancellationToken: ct));

            // 3. Re-insert manageable list
            if (manageableRolePublicIds != null && manageableRolePublicIds.Any())
            {
                foreach (var allowedPublicId in manageableRolePublicIds)
                {
                    await connection.ExecuteAsync(
                        new CommandDefinition(insertManageableSql, new { roleId, allowedPublicId, appId }, transaction, cancellationToken: ct));
                }
            }

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }
}
