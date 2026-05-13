using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class AppRepository : BaseRepository, IAppRepository
{
    private const string SelectColumns = "Id, PublicId, TenantId, OwnerId, Name, Description, Icon, Color, Status, IsDeleted, CreatedOn, CreatedBy, ModifiedOn, ModifiedBy, DeletedOn, DeletedBy, RowVersion";

    private const string GetByPublicIdSql = $"""
        SELECT {SelectColumns}
        FROM meta.App
        WHERE TenantId = @tenantId
          AND PublicId = @publicId
          AND IsDeleted = 0
        """;

    private const string ListSql = $"""
        SELECT {SelectColumns}
        FROM meta.App
        WHERE TenantId = @tenantId
          AND IsDeleted = 0
        ORDER BY Name
        OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY
        """;

    private const string CountSql = """
        SELECT COUNT(1)
        FROM meta.App
        WHERE TenantId = @tenantId
          AND IsDeleted = 0
        """;

    private const string NameExistsSql = """
        SELECT CAST(CASE WHEN EXISTS (
            SELECT 1 FROM meta.App
            WHERE TenantId = @tenantId AND Name = @name AND IsDeleted = 0
        ) THEN 1 ELSE 0 END AS BIT)
        """;

    private const string InsertSql = """
        INSERT INTO meta.App (TenantId, OwnerId, Name, Description, Icon, Color, Status, IsDeleted, CreatedOn, CreatedBy)
        OUTPUT INSERTED.PublicId
        VALUES (@tenantId, @ownerId, @name, @description, @icon, @color, @status, 0, SYSUTCDATETIME(), @createdBy)
        """;

    private const string SoftDeleteSql = """
        UPDATE meta.App
        SET IsDeleted = 1,
            ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy,
            DeletedOn  = SYSUTCDATETIME(), DeletedBy  = @modifiedBy
        WHERE TenantId = @tenantId
          AND PublicId = @publicId
          AND IsDeleted = 0
        """;

    public AppRepository(DbConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    public async Task<App> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var app = await connection.QuerySingleOrDefaultAsync<App>(
            new CommandDefinition(GetByPublicIdSql, new { tenantId = QueryContext.TenantId, publicId }, cancellationToken: ct));
        return app ?? throw new NotFoundException("App", publicId);
    }

    public async Task<IReadOnlyList<App>> ListAsync(int page, int pageSize, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var results = await connection.QueryAsync<App>(
            new CommandDefinition(ListSql, new { tenantId = QueryContext.TenantId, offset = (page - 1) * pageSize, pageSize }, cancellationToken: ct));
        return results.AsList();
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(CountSql, new { tenantId = QueryContext.TenantId }, cancellationToken: ct));
    }

    public async Task<bool> NameExistsAsync(string name, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(NameExistsSql, new { tenantId = QueryContext.TenantId, name }, cancellationToken: ct));
    }

    public async Task<Guid> CreateAsync(App app, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.ExecuteScalarAsync<Guid>(
            new CommandDefinition(InsertSql, new
            {
                tenantId = QueryContext.TenantId,
                ownerId = app.OwnerId,
                name = app.Name,
                description = app.Description,
                icon = app.Icon,
                color = app.Color,
                status = app.Status,
                createdBy = QueryContext.UserId,
            }, cancellationToken: ct));
    }

    public async Task DeleteAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(SoftDeleteSql, new { tenantId = QueryContext.TenantId, publicId, modifiedBy = QueryContext.UserId }, cancellationToken: ct));
        if (affected == 0)
            throw new NotFoundException("App", publicId);
    }
}
