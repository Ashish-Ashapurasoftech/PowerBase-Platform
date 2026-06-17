using System.Data;
using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Tenants;
using PowerBase.Application.Users;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class TenantRepository : ControlRepositoryBase, ITenantRepository
{
    private const string SlugExistsSql = """
        SELECT CAST(CASE WHEN EXISTS (
            SELECT 1 FROM meta.Tenant WHERE Slug = @slug AND IsDeleted = 0
        ) THEN 1 ELSE 0 END AS BIT)
        """;

    private const string GetTenantNameByIdSql = """
        SELECT Name FROM meta.Tenant WHERE Id = @tenantId AND IsDeleted = 0
        """;

    private const string GetActiveTenantIdByUserIdSql = """
        SELECT TOP 1 tu.TenantId
        FROM meta.TenantUser tu
        JOIN meta.Tenant t ON t.Id = tu.TenantId
        WHERE tu.UserId = @userId
          AND tu.IsActive = 1
          AND tu.IsDeleted = 0
          AND t.IsDeleted = 0
        ORDER BY tu.Id
        """;

    private const string ListTenantsForUserSql = """
        SELECT t.PublicId, t.Name, t.Slug, tu.IsOwner
        FROM meta.TenantUser tu
        JOIN meta.Tenant t ON t.Id = tu.TenantId
        WHERE tu.UserId  = @userId
          AND tu.IsActive = 1
          AND tu.IsDeleted = 0
          AND t.IsDeleted  = 0
          AND t.Status     = 'Active'
        ORDER BY tu.Id
        """;

    private const string GetTenantForUserSql = """
        SELECT t.Id, t.PublicId, t.Name, t.Slug, t.PlanCode, t.Status, t.IsDeleted,
               t.CreatedOn, t.CreatedBy, t.ModifiedOn, t.ModifiedBy, t.DeletedOn, t.DeletedBy
        FROM meta.Tenant t
        JOIN meta.TenantUser tu ON tu.TenantId = t.Id
        WHERE t.PublicId   = @tenantPublicId
          AND tu.UserId    = @userId
          AND tu.IsActive  = 1
          AND tu.IsDeleted = 0
          AND t.IsDeleted  = 0
        """;

    private const string InsertTenantSql = """
        INSERT INTO meta.Tenant (PublicId, Name, Slug, PlanCode, Status, ProvisioningState, IsDeleted, CreatedOn, CreatedBy)
        OUTPUT INSERTED.Id
        VALUES (@publicId, @name, @slug, 'Free', 'Active', 'Provisioning', 0, SYSUTCDATETIME(), 0)
        """;

    private const string UpdateProvisioningSql = """
        UPDATE meta.Tenant
        SET ProvisioningState    = @provisioningState,
            DatabaseName         = @databaseName,
            SchemaVersion        = @schemaVersion,
            ServerRef            = @serverRef,
            ConnectionSecretRef  = @connectionSecretRef,
            ModifiedOn           = SYSUTCDATETIME()
        WHERE Id = @tenantId
        """;

    private const string InsertTenantRoleSql = """
        INSERT INTO meta.TenantRole (TenantId, Name, IsDefault, IsSystem, IsDeleted, CreatedOn, CreatedBy)
        OUTPUT INSERTED.Id
        VALUES (@tenantId, @name, @isDefault, @isSystem, 0, SYSUTCDATETIME(), 0)
        """;

    private const string InsertTenantUserSql = """
        INSERT INTO meta.TenantUser (TenantId, UserId, TenantRoleId, IsOwner, IsActive, IsDeleted, JoinedOn, InvitedBy, CreatedOn, CreatedBy)
        VALUES (@tenantId, @userId, @tenantRoleId, @isOwner, @isActive, 0, SYSUTCDATETIME(), @invitedBy, SYSUTCDATETIME(), @createdBy)
        """;


    private const string ListUsersSql = """
        SELECT u.PublicId AS UserPublicId, u.Email, u.Name,
               r.PublicId AS RolePublicId, r.Name AS RoleName,
               tu.IsOwner, tu.IsActive, tu.JoinedOn
        FROM meta.TenantUser tu
        JOIN core.[User] u ON u.Id = tu.UserId
        LEFT JOIN meta.TenantRole r ON r.Id = tu.TenantRoleId AND r.IsDeleted = 0
        WHERE tu.TenantId = @tenantId
          AND tu.IsDeleted = 0
        """;
    private const string GetTenantUserByUserPublicIdSql = """
        SELECT tu.Id, tu.TenantId, tu.UserId, tu.TenantRoleId, tu.IsOwner, tu.IsActive, tu.IsDeleted,
               tu.JoinedOn, tu.InvitedBy, tu.CreatedOn, tu.CreatedBy, tu.ModifiedOn, tu.ModifiedBy,
               tu.DeletedOn, tu.DeletedBy, tu.RowVersion
        FROM meta.TenantUser tu
        JOIN core.[User] u ON u.Id = tu.UserId
        WHERE u.PublicId = @userPublicId
          AND tu.TenantId = @tenantId
          AND tu.IsDeleted = 0
        """;

    private const string IsUserInTenantSql = """
        SELECT CAST(CASE WHEN EXISTS (
            SELECT 1 FROM meta.TenantUser
            WHERE UserId = @userId AND TenantId = @tenantId AND IsDeleted = 0
        ) THEN 1 ELSE 0 END AS BIT)
        """;

    private const string IsActiveMemberSql = """
        SELECT CAST(CASE WHEN EXISTS (
            SELECT 1 FROM meta.TenantUser
            WHERE UserId = @userId AND TenantId = @tenantId AND IsActive = 1 AND IsDeleted = 0
        ) THEN 1 ELSE 0 END AS BIT)
        """;

    private const string UpdateTenantUserRoleSql = """
        UPDATE meta.TenantUser
        SET TenantRoleId = @tenantRoleId, ModifiedOn = SYSUTCDATETIME()
        WHERE Id = @id
        """;

    private const string RemoveTenantUserSql = """
        UPDATE meta.TenantUser
        SET IsDeleted = 1, DeletedOn = SYSUTCDATETIME()
        WHERE Id = @id
        """;

    private const string ActivateTenantUserSql = """
        UPDATE meta.TenantUser
        SET IsActive = 1, ModifiedOn = SYSUTCDATETIME()
        WHERE UserId = @userId AND TenantId = @tenantId AND IsDeleted = 0
        """;

    private const string UpsertTenantUserSql = """
        MERGE meta.TenantUser AS target
        USING (VALUES (@tenantId, @userId)) AS source (TenantId, UserId)
        ON target.TenantId = source.TenantId AND target.UserId = source.UserId
        WHEN MATCHED THEN
            UPDATE SET IsDeleted = 0, DeletedOn = NULL, DeletedBy = NULL,
                       TenantRoleId = @tenantRoleId, IsOwner = @isOwner,
                       IsActive = @isActive, InvitedBy = @invitedBy,
                       ModifiedOn = SYSUTCDATETIME()
        WHEN NOT MATCHED THEN
            INSERT (TenantId, UserId, TenantRoleId, IsOwner, IsActive, IsDeleted, JoinedOn, InvitedBy, CreatedOn, CreatedBy)
            VALUES (@tenantId, @userId, @tenantRoleId, @isOwner, @isActive, 0, SYSUTCDATETIME(), @invitedBy, SYSUTCDATETIME(), @createdBy);
        """;

    private const string ListRolesSql = """
        SELECT Id, PublicId, TenantId, Name, Description, IsDefault, IsSystem, IsDeleted,
               CreatedOn, CreatedBy, ModifiedOn, ModifiedBy, DeletedOn, DeletedBy
        FROM meta.TenantRole
        WHERE TenantId = @tenantId AND IsDeleted = 0
        ORDER BY IsSystem DESC, Name
        """;

    private const string GetRoleByIdSql = """
        SELECT Id, PublicId, TenantId, Name, Description, IsDefault, IsSystem, IsDeleted,
               CreatedOn, CreatedBy, ModifiedOn, ModifiedBy, DeletedOn, DeletedBy
        FROM meta.TenantRole
        WHERE Id = @id AND TenantId = @tenantId AND IsDeleted = 0
        """;

    private const string GetRoleByPublicIdSql = """
        SELECT Id, PublicId, TenantId, Name, Description, IsDefault, IsSystem, IsDeleted,
               CreatedOn, CreatedBy, ModifiedOn, ModifiedBy, DeletedOn, DeletedBy
        FROM meta.TenantRole
        WHERE PublicId = @publicId AND TenantId = @tenantId AND IsDeleted = 0
        """;

    private const string RoleNameExistsSql = """
        SELECT CAST(CASE WHEN EXISTS (
            SELECT 1 FROM meta.TenantRole
            WHERE TenantId = @tenantId AND Name = @name AND IsDeleted = 0
        ) THEN 1 ELSE 0 END AS BIT)
        """;

    private const string UpdateRoleSql = """
        UPDATE meta.TenantRole
        SET Name = @name, Description = @description, ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy
        WHERE TenantId = @tenantId AND PublicId = @publicId AND IsSystem = 0 AND IsDeleted = 0
        """;

    private const string SoftDeleteRoleSql = """
        UPDATE meta.TenantRole
        SET IsDeleted = 1, DeletedOn = SYSUTCDATETIME(), DeletedBy = @deletedBy
        WHERE TenantId = @tenantId AND PublicId = @publicId AND IsSystem = 0 AND IsDeleted = 0
        """;

    private const string CountRoleMembersSql = """
        SELECT COUNT(1) FROM meta.TenantUser
        WHERE TenantId = @tenantId AND TenantRoleId = @roleId AND IsDeleted = 0
        """;

    public TenantRepository(IControlConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    public async Task<string?> GetTenantNameByIdAsync(long tenantId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.ExecuteScalarAsync<string?>(
            new CommandDefinition(GetTenantNameByIdSql, new { tenantId }, cancellationToken: ct));
    }

    public async Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(SlugExistsSql, new { slug }, cancellationToken: ct));
    }

    public async Task<long> GetActiveTenantIdByUserIdAsync(long userId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var tenantId = await connection.ExecuteScalarAsync<long?>(
            new CommandDefinition(GetActiveTenantIdByUserIdSql, new { userId }, cancellationToken: ct));
        return tenantId ?? throw new NotFoundException("TenantUser", userId);
    }

    public async Task<long> CreateAsync(Tenant tenant, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        var connection = transaction?.Connection ?? (IDbConnection)(await OpenNewConnectionAsync(ct));
        bool ownConnection = transaction is null;
        try
        {
            return await connection.ExecuteScalarAsync<long>(
                new CommandDefinition(InsertTenantSql, new
                {
                    publicId = tenant.PublicId == Guid.Empty ? Guid.NewGuid() : tenant.PublicId,
                    name = tenant.Name,
                    slug = tenant.Slug,
                }, transaction, cancellationToken: ct));
        }
        finally
        {
            if (ownConnection) connection.Dispose();
        }
    }

    public async Task<long> CreateRoleAsync(TenantRole role, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        var connection = transaction?.Connection ?? (IDbConnection)(await OpenNewConnectionAsync(ct));
        bool ownConnection = transaction is null;
        try
        {
            return await connection.ExecuteScalarAsync<long>(
                new CommandDefinition(InsertTenantRoleSql, new
                {
                    tenantId = role.TenantId,
                    name = role.Name,
                    isDefault = role.IsDefault,
                    isSystem = role.IsSystem,
                }, transaction, cancellationToken: ct));
        }
        finally
        {
            if (ownConnection) connection.Dispose();
        }
    }

    public async Task CreateTenantUserAsync(TenantUser tenantUser, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        var connection = transaction?.Connection ?? (IDbConnection)(await OpenNewConnectionAsync(ct));
        bool ownConnection = transaction is null;
        try
        {
            await connection.ExecuteAsync(
                new CommandDefinition(InsertTenantUserSql, new
                {
                    tenantId = tenantUser.TenantId,
                    userId = tenantUser.UserId,
                    tenantRoleId = tenantUser.TenantRoleId,
                    isOwner = tenantUser.IsOwner,
                    isActive = tenantUser.IsActive,
                    invitedBy = tenantUser.InvitedBy,
                    createdBy = tenantUser.CreatedBy,
                }, transaction, cancellationToken: ct));
        }
        finally
        {
            if (ownConnection) connection.Dispose();
        }
    }

    public async Task UpsertTenantUserAsync(TenantUser tenantUser, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        await connection.ExecuteAsync(
            new CommandDefinition(UpsertTenantUserSql, new
            {
                tenantId = tenantUser.TenantId,
                userId = tenantUser.UserId,
                tenantRoleId = tenantUser.TenantRoleId,
                isOwner = tenantUser.IsOwner,
                isActive = tenantUser.IsActive,
                invitedBy = tenantUser.InvitedBy,
                createdBy = tenantUser.CreatedBy,
            }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<TenantUserDetail>> ListUsersAsync(string? searchTerm = null, string? roleName = null, string? status = null, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();

        var sql = new System.Text.StringBuilder(ListUsersSql);

        var parameters = new DynamicParameters();
        parameters.Add("tenantId", QueryContext.TenantId);

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            sql.Append(" AND (u.Name LIKE @SearchTerm OR u.Email LIKE @SearchTerm)");
            parameters.Add("SearchTerm", $"%{searchTerm}%");
        }

        if (!string.IsNullOrWhiteSpace(roleName))
        {
            sql.Append(" AND r.Name = @RoleName");
            parameters.Add("RoleName", roleName);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (status.Equals("active", StringComparison.OrdinalIgnoreCase))
            {
                sql.Append(" AND tu.IsActive = 1");
            }
            else if (status.Equals("pending", StringComparison.OrdinalIgnoreCase))
            {
                sql.Append(" AND tu.IsActive = 0");
            }
        }

        sql.Append(" ORDER BY tu.JoinedOn");

        var rows = await connection.QueryAsync<TenantUserDetail>(
            new CommandDefinition(sql.ToString(), parameters, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<TenantUser?> GetTenantUserByUserPublicIdAsync(Guid userPublicId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<TenantUser>(
            new CommandDefinition(GetTenantUserByUserPublicIdSql, new { userPublicId, tenantId = QueryContext.TenantId }, cancellationToken: ct));
    }

    public async Task<bool> IsUserInTenantAsync(long userId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(IsUserInTenantSql, new { userId, tenantId = QueryContext.TenantId }, cancellationToken: ct));
    }

    public async Task<bool> IsActiveMemberAsync(long userId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(IsActiveMemberSql, new { userId, tenantId = QueryContext.TenantId }, cancellationToken: ct));
    }

    public async Task UpdateTenantUserRoleAsync(long tenantUserId, long tenantRoleId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        await connection.ExecuteAsync(
            new CommandDefinition(UpdateTenantUserRoleSql, new { id = tenantUserId, tenantRoleId }, cancellationToken: ct));
    }

    public async Task RemoveTenantUserAsync(long tenantUserId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        await connection.ExecuteAsync(
            new CommandDefinition(RemoveTenantUserSql, new { id = tenantUserId }, cancellationToken: ct));
    }

    public async Task ActivateTenantUserAsync(long userId, long tenantId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        await connection.ExecuteAsync(
            new CommandDefinition(ActivateTenantUserSql, new { userId, tenantId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<TenantRole>> ListRolesAsync(CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var results = await connection.QueryAsync<TenantRole>(
            new CommandDefinition(ListRolesSql, new { tenantId = QueryContext.TenantId }, cancellationToken: ct));
        return results.ToList();
    }

    public async Task<TenantRole?> GetRoleByIdAsync(long id, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<TenantRole>(
            new CommandDefinition(GetRoleByIdSql, new { id, tenantId = QueryContext.TenantId }, cancellationToken: ct));
    }

    public async Task<TenantRole?> GetRoleByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<TenantRole>(
            new CommandDefinition(GetRoleByPublicIdSql, new { publicId, tenantId = QueryContext.TenantId }, cancellationToken: ct));
    }

    public async Task<bool> RoleNameExistsAsync(string name, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(RoleNameExistsSql, new { tenantId = QueryContext.TenantId, name }, cancellationToken: ct));
    }

    public async Task<int> UpdateRoleAsync(Guid publicId, string name, string? description, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.ExecuteAsync(
            new CommandDefinition(UpdateRoleSql, new { publicId, name, description, tenantId = QueryContext.TenantId, modifiedBy = QueryContext.UserId }, cancellationToken: ct));
    }

    public async Task<int> DeleteRoleAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.ExecuteAsync(
            new CommandDefinition(SoftDeleteRoleSql, new { publicId, tenantId = QueryContext.TenantId, deletedBy = QueryContext.UserId }, cancellationToken: ct));
    }

    public async Task<int> CountRoleMembersAsync(long roleId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(CountRoleMembersSql, new { tenantId = QueryContext.TenantId, roleId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<TenantItem>> ListTenantsForUserAsync(long userId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var rows = await connection.QueryAsync<TenantItem>(
            new CommandDefinition(ListTenantsForUserSql, new { userId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<Tenant> GetTenantForUserAsync(Guid tenantPublicId, long userId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var tenant = await connection.QuerySingleOrDefaultAsync<Tenant>(
            new CommandDefinition(GetTenantForUserSql, new { tenantPublicId, userId }, cancellationToken: ct));
        return tenant ?? throw new NotFoundException("Tenant", tenantPublicId);
    }

    public async Task UpdateProvisioningAsync(long tenantId, string provisioningState, string? databaseName, int schemaVersion = 0, string? serverRef = null, string? connectionSecretRef = null, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(UpdateProvisioningSql, new
        {
            tenantId,
            provisioningState,
            databaseName,
            schemaVersion,
            serverRef,
            connectionSecretRef,
        }, cancellationToken: ct));
    }

}
