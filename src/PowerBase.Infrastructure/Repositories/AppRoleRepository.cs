using System.Data;
using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class AppRoleRepository : BaseRepository, IAppRoleRepository
{
    private const string SelectColumns = "Id, PublicId, AppId, TenantId, Name, IsDefault, IsSystem, CreatedOn, CreatedBy, IsDeleted";

    private const string ListByAppIdSql = $"""
        SELECT {SelectColumns}
        FROM meta.AppRole
        WHERE AppId = @appId AND TenantId = @tenantId AND IsDeleted = 0
        ORDER BY IsSystem DESC, Name
        """;

    private const string GetByPublicIdSql = $"""
        SELECT {SelectColumns}
        FROM meta.AppRole
        WHERE PublicId = @publicId AND TenantId = @tenantId AND IsDeleted = 0
        """;

    private const string NameExistsSql = """
        SELECT CAST(CASE WHEN EXISTS (
            SELECT 1 FROM meta.AppRole
            WHERE AppId = @appId AND TenantId = @tenantId AND Name = @name AND IsDeleted = 0
        ) THEN 1 ELSE 0 END AS BIT)
        """;

    private const string InsertSql = """
        INSERT INTO meta.AppRole (AppId, TenantId, Name, IsDefault, IsSystem, CreatedOn, CreatedBy)
        OUTPUT INSERTED.Id, INSERTED.PublicId
        VALUES (@appId, @tenantId, @name, @isDefault, @isSystem, SYSUTCDATETIME(), @createdBy)
        """;

    private const string SoftDeleteSql = """
        UPDATE meta.AppRole
        SET IsDeleted = 1
        WHERE PublicId = @publicId AND TenantId = @tenantId AND IsSystem = 0 AND IsDeleted = 0
        """;

    public AppRoleRepository(DbConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    public async Task<IReadOnlyList<AppRole>> ListByAppIdAsync(long appId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var results = await connection.QueryAsync<AppRole>(
            new CommandDefinition(ListByAppIdSql, new { appId, tenantId = QueryContext.TenantId }, cancellationToken: ct));
        return results.AsList();
    }

    public async Task<AppRole?> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<AppRole>(
            new CommandDefinition(GetByPublicIdSql, new { publicId, tenantId = QueryContext.TenantId }, cancellationToken: ct));
    }

    public async Task<bool> NameExistsInAppAsync(long appId, string name, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(NameExistsSql, new { appId, tenantId = QueryContext.TenantId, name }, cancellationToken: ct));
    }

    public async Task<(long Id, Guid PublicId)> CreateAsync(AppRole role, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        var parameters = new { appId = role.AppId, tenantId = QueryContext.TenantId, name = role.Name, isDefault = role.IsDefault, isSystem = role.IsSystem, createdBy = QueryContext.UserId };

        if (transaction is not null)
            return await transaction.Connection!.QuerySingleAsync<(long Id, Guid PublicId)>(
                new CommandDefinition(InsertSql, parameters, transaction, cancellationToken: ct));

        await using var connection = ConnectionFactory.Create();
        return await connection.QuerySingleAsync<(long Id, Guid PublicId)>(
            new CommandDefinition(InsertSql, parameters, cancellationToken: ct));
    }

    public async Task DeleteAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        await connection.ExecuteAsync(
            new CommandDefinition(SoftDeleteSql, new { publicId, tenantId = QueryContext.TenantId }, cancellationToken: ct));
    }
}
