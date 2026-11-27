using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class AppVariableRepository : TenantRepositoryBase, IAppVariableRepository
{
    private const string SelectColumns = "Id, PublicId, AppId, Name, Value, Description, IsDeleted, CreatedOn, CreatedBy, ModifiedOn, ModifiedBy, DeletedOn, DeletedBy, RowVersion";

    private const string ListSql = $"""
        SELECT {SelectColumns}
        FROM meta.AppVariable
        WHERE AppId = @appId
          AND IsDeleted = 0
        ORDER BY Name
        """;

    private const string GetByPublicIdSql = $"""
        SELECT {SelectColumns}
        FROM meta.AppVariable
        WHERE AppId = @appId
          AND PublicId = @publicId
          AND IsDeleted = 0
        """;

    private const string CountSql = """
        SELECT COUNT(1)
        FROM meta.AppVariable
        WHERE AppId = @appId AND IsDeleted = 0
        """;

    private const string NameExistsSql = """
        SELECT CAST(CASE WHEN EXISTS (
            SELECT 1 FROM meta.AppVariable
            WHERE AppId = @appId AND Name = @name AND IsDeleted = 0
        ) THEN 1 ELSE 0 END AS BIT)
        """;

    private const string InsertSql = """
        INSERT INTO meta.AppVariable (AppId, Name, Value, Description, CreatedBy)
        OUTPUT INSERTED.PublicId
        VALUES (@appId, @name, @value, @description, @createdBy)
        """;

    private const string UpdateSql = """
        UPDATE meta.AppVariable
        SET Name        = @name,
            Value       = @value,
            Description = @description,
            ModifiedOn  = SYSUTCDATETIME(),
            ModifiedBy  = @modifiedBy
        WHERE AppId     = @appId
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
          AND PublicId  = @publicId
          AND IsDeleted = 0
        """;

    private readonly IMetadataCache _cache;

    public AppVariableRepository(ITenantConnectionFactory connectionFactory, IQueryContext queryContext, IMetadataCache cache)
        : base(connectionFactory, queryContext)
    {
        _cache = cache;
    }

    /// <summary>Drops the cached variable lists of this tenant. Called BEFORE each write below.</summary>
    private void VariablesChanging() => _cache.Invalidate(QueryContext.TenantId, MetadataCacheGroups.Variables);

    public async Task<IReadOnlyList<AppVariable>> ListAsync(long appId, CancellationToken ct = default)
    {
        // Read by formulas and pipelines on many requests. Each caller gets its own copies.
        var cached = await _cache.GetOrAddAsync<IReadOnlyList<AppVariable>>(QueryContext.TenantId, MetadataCacheGroups.Variables, $"app:{appId}", async () =>
        {
            await using var connection = await ConnectionFactory.CreateAsync(ct);
            var results = await connection.QueryAsync<AppVariable>(
                new CommandDefinition(ListSql, new { appId }, cancellationToken: ct));
            return results.AsList();
        });
        return cached.Select(v => new AppVariable
        {
            Id = v.Id, PublicId = v.PublicId, AppId = v.AppId, TenantId = v.TenantId, Name = v.Name, Value = v.Value,
            Description = v.Description, IsDeleted = v.IsDeleted, CreatedOn = v.CreatedOn, CreatedBy = v.CreatedBy,
            ModifiedOn = v.ModifiedOn, ModifiedBy = v.ModifiedBy, DeletedOn = v.DeletedOn, DeletedBy = v.DeletedBy, RowVersion = v.RowVersion,
        }).ToList();
    }

    public async Task<AppVariable?> GetByPublicIdAsync(long appId, Guid publicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<AppVariable>(
            new CommandDefinition(GetByPublicIdSql, new { appId, publicId }, cancellationToken: ct));
    }

    public async Task<int> CountAsync(long appId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(CountSql, new { appId }, cancellationToken: ct));
    }

    public async Task<bool> NameExistsAsync(long appId, string name, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(NameExistsSql, new { appId, name }, cancellationToken: ct));
    }

    public async Task<Guid> CreateAsync(AppVariable variable, CancellationToken ct = default)
    {
        VariablesChanging();
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.QuerySingleAsync<Guid>(
            new CommandDefinition(InsertSql, new
            {
                appId = variable.AppId,
                name = variable.Name,
                value = variable.Value,
                description = variable.Description,
                createdBy = QueryContext.UserId,
            }, cancellationToken: ct));
    }

    public async Task UpdateAsync(long appId, Guid publicId, string name, string value, string? description, CancellationToken ct = default)
    {
        VariablesChanging();
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(UpdateSql, new { appId, publicId, name, value, description, modifiedBy = QueryContext.UserId }, cancellationToken: ct));
        if (affected == 0)
            throw new NotFoundException("AppVariable", publicId);
    }

    public async Task DeleteAsync(long appId, Guid publicId, CancellationToken ct = default)
    {
        VariablesChanging();
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(SoftDeleteSql, new { appId, publicId, deletedBy = QueryContext.UserId }, cancellationToken: ct));
        if (affected == 0)
            throw new NotFoundException("AppVariable", publicId);
    }
}
