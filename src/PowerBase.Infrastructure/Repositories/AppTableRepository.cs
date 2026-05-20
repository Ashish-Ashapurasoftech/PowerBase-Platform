using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class AppTableRepository : BaseRepository, IAppTableRepository
{
    private const string SelectColumns = """
        Id, PublicId, TenantId, AppId, Name, SingularLabel, PluralLabel, Description,
        PhysicalTableName, DisplayFieldId, RecordCount, IsSystem, DisplayOrder,
        IsDeleted, CreatedOn, CreatedBy, ModifiedOn, ModifiedBy, DeletedOn, DeletedBy, RowVersion
        """;

    private const string GetByIdSql = $"""
        SELECT {SelectColumns}
        FROM meta.AppTable
        WHERE TenantId = @tenantId
          AND Id = @id
          AND IsDeleted = 0
        """;

    private const string GetByPublicIdSql = $"""
        SELECT {SelectColumns}
        FROM meta.AppTable
        WHERE TenantId = @tenantId
          AND PublicId = @publicId
          AND IsDeleted = 0
        """;

    private const string GetAppIdByPublicIdSql = """
        SELECT AppId FROM meta.AppTable
        WHERE TenantId = @tenantId AND PublicId = @publicId AND IsDeleted = 0
        """;

    private const string ListByAppSql = $"""
        SELECT {SelectColumns}
        FROM meta.AppTable
        WHERE TenantId = @tenantId
          AND AppId = @appId
          AND IsDeleted = 0
        ORDER BY DisplayOrder, Name
        """;

    private const string NameExistsSql = """
        SELECT CAST(CASE WHEN EXISTS (
            SELECT 1 FROM meta.AppTable
            WHERE TenantId = @tenantId AND AppId = @appId AND Name = @name AND IsDeleted = 0
        ) THEN 1 ELSE 0 END AS BIT)
        """;

    private const string InsertSql = """
        INSERT INTO meta.AppTable (TenantId, AppId, Name, SingularLabel, PluralLabel, Description, Icon, IsDeleted, CreatedOn, CreatedBy)
        OUTPUT INSERTED.Id, INSERTED.PublicId
        VALUES (@tenantId, @appId, @name, @singularLabel, @pluralLabel, @description, @icon, 0, SYSUTCDATETIME(), @createdBy)
        """;

    private const string UpdatePhysicalNameSql = """
        UPDATE meta.AppTable SET PhysicalTableName = @physicalTableName WHERE Id = @id
        """;

    private const string UpdateTableSql = """
        UPDATE meta.AppTable
        SET Name          = @name,
            SingularLabel = @singularLabel,
            PluralLabel   = @pluralLabel,
            Description   = @description,
            Icon          = @icon,
            ModifiedOn    = SYSUTCDATETIME(),
            ModifiedBy    = @modifiedBy
        WHERE TenantId = @tenantId AND PublicId = @publicId AND IsDeleted = 0
        """;

    private const string SoftDeleteSql = """
        UPDATE meta.AppTable
        SET IsDeleted = 1,
            ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy,
            DeletedOn  = SYSUTCDATETIME(), DeletedBy  = @modifiedBy
        WHERE TenantId = @tenantId
          AND PublicId = @publicId
          AND IsDeleted = 0
        """;

    public AppTableRepository(DbConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    public async Task<AppTable> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var table = await connection.QuerySingleOrDefaultAsync<AppTable>(
            new CommandDefinition(GetByIdSql, new { tenantId = QueryContext.TenantId, id }, cancellationToken: ct));
        return table ?? throw new NotFoundException("Table", id);
    }

    public async Task<AppTable> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var table = await connection.QuerySingleOrDefaultAsync<AppTable>(
            new CommandDefinition(GetByPublicIdSql, new { tenantId = QueryContext.TenantId, publicId }, cancellationToken: ct));
        return table ?? throw new NotFoundException("Table", publicId);
    }

    public async Task<long> GetAppIdByPublicIdAsync(Guid tablePublicId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var appId = await connection.ExecuteScalarAsync<long?>(
            new CommandDefinition(GetAppIdByPublicIdSql, new { tenantId = QueryContext.TenantId, publicId = tablePublicId }, cancellationToken: ct));
        return appId ?? throw new NotFoundException("Table", tablePublicId);
    }

    public async Task<IReadOnlyList<AppTable>> ListByAppAsync(long appId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var results = await connection.QueryAsync<AppTable>(
            new CommandDefinition(ListByAppSql, new { tenantId = QueryContext.TenantId, appId }, cancellationToken: ct));
        return results.AsList();
    }

    public async Task<bool> NameExistsInAppAsync(long appId, string name, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(NameExistsSql, new { tenantId = QueryContext.TenantId, appId, name }, cancellationToken: ct));
    }

    public async Task<(long Id, Guid PublicId)> CreateAsync(AppTable table, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var row = await connection.QuerySingleAsync(
            new CommandDefinition(InsertSql, new
            {
                tenantId = QueryContext.TenantId,
                appId = table.AppId,
                name = table.Name,
                singularLabel = table.SingularLabel,
                pluralLabel = table.PluralLabel,
                description = table.Description,
                icon = table.Icon,
                createdBy = QueryContext.UserId,
            }, cancellationToken: ct));
        return ((long)row.Id, (Guid)row.PublicId);
    }

    public async Task UpdatePhysicalNameAsync(long id, string physicalTableName, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        await connection.ExecuteAsync(
            new CommandDefinition(UpdatePhysicalNameSql, new { id, physicalTableName }, cancellationToken: ct));
    }

    public async Task<int> UpdateAsync(Guid publicId, string name, string? singularLabel, string? pluralLabel, string? description, string? icon, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.ExecuteAsync(
            new CommandDefinition(UpdateTableSql, new
            {
                tenantId = QueryContext.TenantId,
                publicId, name, singularLabel, pluralLabel, description, icon,
                modifiedBy = QueryContext.UserId,
            }, cancellationToken: ct));
    }

    public async Task DeleteAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(SoftDeleteSql, new { tenantId = QueryContext.TenantId, publicId, modifiedBy = QueryContext.UserId }, cancellationToken: ct));
        if (affected == 0)
            throw new NotFoundException("Table", publicId);
    }
}
