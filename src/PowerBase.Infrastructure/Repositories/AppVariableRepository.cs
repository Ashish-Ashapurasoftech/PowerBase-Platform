using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class AppVariableRepository : BaseRepository, IAppVariableRepository
{
    private const string SelectColumns = "Id, PublicId, AppId, TenantId, Name, Value, Description, IsDeleted, CreatedOn, CreatedBy, ModifiedOn, ModifiedBy, DeletedOn, DeletedBy, RowVersion";

    private const string ListSql = $"""
        SELECT {SelectColumns}
        FROM meta.AppVariable
        WHERE AppId = @appId
          AND TenantId = @tenantId
          AND IsDeleted = 0
        ORDER BY Name
        """;

    private const string GetByPublicIdSql = $"""
        SELECT {SelectColumns}
        FROM meta.AppVariable
        WHERE AppId = @appId
          AND TenantId = @tenantId
          AND PublicId = @publicId
          AND IsDeleted = 0
        """;

    private const string CountSql = """
        SELECT COUNT(1)
        FROM meta.AppVariable
        WHERE AppId = @appId
          AND TenantId = @tenantId
          AND IsDeleted = 0
        """;

    private const string NameExistsSql = """
        SELECT CAST(CASE WHEN EXISTS (
            SELECT 1 FROM meta.AppVariable
            WHERE AppId = @appId
              AND TenantId = @tenantId
              AND Name = @name
              AND IsDeleted = 0
        ) THEN 1 ELSE 0 END AS BIT)
        """;

    private const string InsertSql = """
        INSERT INTO meta.AppVariable (AppId, TenantId, Name, Value, Description, CreatedBy)
        OUTPUT INSERTED.PublicId
        VALUES (@appId, @tenantId, @name, @value, @description, @createdBy)
        """;

    private const string UpdateSql = """
        UPDATE meta.AppVariable
        SET Name        = @name,
            Value       = @value,
            Description = @description,
            ModifiedOn  = SYSUTCDATETIME(),
            ModifiedBy  = @modifiedBy
        WHERE AppId     = @appId
          AND TenantId  = @tenantId
          AND PublicId  = @publicId
          AND IsDeleted = 0
        """;

    private const string SoftDeleteSql = """
        UPDATE meta.AppVariable
        SET IsDeleted   = 1,
            ModifiedOn  = SYSUTCDATETIME(),
            ModifiedBy  = @deletedBy,
            DeletedOn   = SYSUTCDATETIME(),
            DeletedBy   = @deletedBy
        WHERE AppId     = @appId
          AND TenantId  = @tenantId
          AND PublicId  = @publicId
          AND IsDeleted = 0
        """;

    public AppVariableRepository(DbConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    public async Task<IReadOnlyList<AppVariable>> ListAsync(long appId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var results = await connection.QueryAsync<AppVariable>(
            new CommandDefinition(ListSql, new { appId, tenantId = QueryContext.TenantId }, cancellationToken: ct));
        return results.AsList();
    }

    public async Task<AppVariable?> GetByPublicIdAsync(long appId, Guid publicId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<AppVariable>(
            new CommandDefinition(GetByPublicIdSql, new { appId, tenantId = QueryContext.TenantId, publicId }, cancellationToken: ct));
    }

    public async Task<int> CountAsync(long appId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(CountSql, new { appId, tenantId = QueryContext.TenantId }, cancellationToken: ct));
    }

    public async Task<bool> NameExistsAsync(long appId, string name, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(NameExistsSql, new { appId, tenantId = QueryContext.TenantId, name }, cancellationToken: ct));
    }

    public async Task<Guid> CreateAsync(AppVariable variable, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.QuerySingleAsync<Guid>(
            new CommandDefinition(InsertSql, new
            {
                appId = variable.AppId,
                tenantId = QueryContext.TenantId,
                name = variable.Name,
                value = variable.Value,
                description = variable.Description,
                createdBy = QueryContext.UserId
            }, cancellationToken: ct));
    }

    public async Task UpdateAsync(long appId, Guid publicId, string name, string value, string? description, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(UpdateSql, new
            {
                appId,
                tenantId = QueryContext.TenantId,
                publicId,
                name,
                value,
                description,
                modifiedBy = QueryContext.UserId
            }, cancellationToken: ct));

        if (affected == 0)
            throw new NotFoundException("AppVariable", publicId);
    }

    public async Task DeleteAsync(long appId, Guid publicId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(SoftDeleteSql, new
            {
                appId,
                tenantId = QueryContext.TenantId,
                publicId,
                deletedBy = QueryContext.UserId
            }, cancellationToken: ct));

        if (affected == 0)
            throw new NotFoundException("AppVariable", publicId);
    }
}
