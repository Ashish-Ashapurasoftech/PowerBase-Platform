using System.Data;
using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class TenantRepository : BaseRepository, ITenantRepository
{
    private const string SlugExistsSql = """
        SELECT CAST(CASE WHEN EXISTS (
            SELECT 1 FROM meta.Tenant WHERE Slug = @slug AND IsDeleted = 0
        ) THEN 1 ELSE 0 END AS BIT)
        """;

    private const string GetActiveTenantIdByUserIdSql = """
        SELECT TOP 1 tu.TenantId
        FROM meta.TenantUser tu
        JOIN meta.Tenant t ON t.Id = tu.TenantId
        WHERE tu.UserId = @userId
          AND tu.IsActive = 1
          AND t.IsDeleted = 0
        ORDER BY tu.Id
        """;

    private const string InsertTenantSql = """
        INSERT INTO meta.Tenant (Name, Slug, Status, IsDeleted, CreatedAt, UpdatedAt)
        OUTPUT INSERTED.Id
        VALUES (@name, @slug, @status, 0, SYSUTCDATETIME(), SYSUTCDATETIME())
        """;

    private const string InsertTenantRoleSql = """
        INSERT INTO meta.TenantRole (TenantId, Name, IsDefault, CreatedAt, UpdatedAt)
        OUTPUT INSERTED.Id
        VALUES (@tenantId, @name, @isDefault, SYSUTCDATETIME(), SYSUTCDATETIME())
        """;

    private const string InsertTenantUserSql = """
        INSERT INTO meta.TenantUser (TenantId, UserId, TenantRoleId, IsActive, CreatedAt, UpdatedAt)
        VALUES (@tenantId, @userId, @tenantRoleId, @isActive, SYSUTCDATETIME(), SYSUTCDATETIME())
        """;

    public TenantRepository(DbConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

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
                    name = tenant.Name,
                    slug = tenant.Slug,
                    status = (int)tenant.Status,
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
                    isActive = tenantUser.IsActive,
                }, transaction, cancellationToken: ct));
        }
        finally
        {
            if (ownConnection) connection.Dispose();
        }
    }

    private async Task<IDbConnection> OpenNewConnectionAsync(CancellationToken ct)
    {
        var conn = ConnectionFactory.Create();
        await conn.OpenAsync(ct);
        return conn;
    }
}
