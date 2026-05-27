using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Application.Common.Models;
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

    private const string ListByUserSql = """
        SELECT a.Id, a.PublicId, a.TenantId, a.OwnerId, a.Name, a.Description, a.Icon, a.Color,
               a.Status, a.IsDeleted, a.CreatedOn, a.CreatedBy, a.ModifiedOn, a.ModifiedBy,
               a.DeletedOn, a.DeletedBy, a.RowVersion, u.Name AS OwnerName
        FROM meta.App a
        JOIN meta.AppUser au ON au.AppId = a.Id
        LEFT JOIN core.[User] u ON u.Id = a.OwnerId
        WHERE a.TenantId  = @tenantId
          AND au.UserId   = @userId
          AND au.IsDeleted = 0
          AND a.IsDeleted  = 0
          AND a.Status = @Status
        ORDER BY a.Name
        OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY
        """;

    private const string CountByUserSql = """
        SELECT COUNT(1)
        FROM meta.App a
        JOIN meta.AppUser au ON au.AppId = a.Id
        WHERE a.TenantId  = @tenantId
          AND au.UserId   = @userId
          AND au.IsDeleted = 0
          AND a.IsDeleted  = 0
        """;

    private const string ListAllByUserSql = """
        SELECT a.Id, a.PublicId, a.TenantId, a.OwnerId, a.Name, a.Description, a.Icon, a.Color,
               a.Status, a.IsDeleted, a.CreatedOn, a.CreatedBy, a.ModifiedOn, a.ModifiedBy,
               a.DeletedOn, a.DeletedBy, a.RowVersion
        FROM meta.App a
        JOIN meta.AppUser au ON au.AppId = a.Id
        WHERE a.TenantId  = @tenantId
          AND au.UserId   = @userId
          AND au.IsDeleted = 0
          AND a.IsDeleted  = 0
        ORDER BY a.Name
        """;

    private const string NameExistsSql = """
        SELECT CAST(CASE WHEN EXISTS (
            SELECT 1 FROM meta.App
            WHERE TenantId = @tenantId AND Name = @name AND IsDeleted = 0
        ) THEN 1 ELSE 0 END AS BIT)
        """;

    private const string GetIdByPublicIdSql = """
        SELECT Id FROM meta.App
        WHERE TenantId = @tenantId AND PublicId = @publicId AND IsDeleted = 0
        """;

    private const string InsertSql = """
        INSERT INTO meta.App (TenantId, OwnerId, Name, Description, Icon, Color, Status, IsDeleted, CreatedOn, CreatedBy)
        OUTPUT INSERTED.PublicId, INSERTED.Id
        VALUES (@tenantId, @ownerId, @name, @description, @icon, @color, @status, 0, SYSUTCDATETIME(), @createdBy)
        """;

    private const string SetDefaultRoleSql = """
        UPDATE meta.App
        SET DefaultAppRoleId = @roleId
        WHERE Id = @appId AND TenantId = @tenantId AND IsDeleted = 0
        """;

    private const string GetDefaultRoleIdSql = """
        SELECT DefaultAppRoleId FROM meta.App
        WHERE Id = @appId AND TenantId = @tenantId AND IsDeleted = 0
        """;

    private const string UpdateSql = """
        UPDATE meta.App
        SET Name        = @name,
            Description = @description,
            Icon        = @icon,
            Color       = @color,
            ModifiedOn  = SYSUTCDATETIME(),
            ModifiedBy  = @modifiedBy
        WHERE TenantId = @tenantId
          AND PublicId  = @publicId
          AND IsDeleted = 0
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
            new CommandDefinition(ListSql, new { tenantId = QueryContext.TenantId, offset = (page - 1) * pageSize, pageSize, }, cancellationToken: ct));
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

    public async Task<long> GetIdByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var id = await connection.ExecuteScalarAsync<long?>(
            new CommandDefinition(GetIdByPublicIdSql, new { tenantId = QueryContext.TenantId, publicId }, cancellationToken: ct));
        return id ?? throw new NotFoundException("App", publicId);
    }

    public async Task<(Guid PublicId, long Id)> CreateAsync(App app, System.Data.IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        var parameters = new
        {
            tenantId = QueryContext.TenantId,
            ownerId = app.OwnerId,
            name = app.Name,
            description = app.Description,
            icon = app.Icon,
            color = app.Color,
            status = app.Status,
            createdBy = QueryContext.UserId,
        };

        if (transaction is not null)
        {
            var row = await transaction.Connection!.QuerySingleAsync<(Guid PublicId, long Id)>(
                new CommandDefinition(InsertSql, parameters, transaction, cancellationToken: ct));
            return row;
        }

        await using var connection = ConnectionFactory.Create();
        return await connection.QuerySingleAsync<(Guid PublicId, long Id)>(
            new CommandDefinition(InsertSql, parameters, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<AppListItemDto>> ListByUserAsync(long userId, int page, int pageSize, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var results = await connection.QueryAsync<AppListItemDto>(
            new CommandDefinition(ListByUserSql, new { tenantId = QueryContext.TenantId, userId,Status = "Active", offset = (page - 1) * pageSize, pageSize }, cancellationToken: ct));
        return results.AsList();
    }

    public async Task<int> CountByUserAsync(long userId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(CountByUserSql, new { tenantId = QueryContext.TenantId, userId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<App>> ListAllByUserAsync(long userId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var results = await connection.QueryAsync<App>(
            new CommandDefinition(ListAllByUserSql, new { tenantId = QueryContext.TenantId, userId }, cancellationToken: ct));
        return results.AsList();
    }

    public async Task SetDefaultRoleAsync(long appId, long roleId, System.Data.IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        var parameters = new { appId, roleId, tenantId = QueryContext.TenantId };
        if (transaction is not null)
        {
            await transaction.Connection!.ExecuteAsync(new CommandDefinition(SetDefaultRoleSql, parameters, transaction, cancellationToken: ct));
            return;
        }
        await using var connection = ConnectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(SetDefaultRoleSql, parameters, cancellationToken: ct));
    }

    public async Task<long?> GetDefaultRoleIdAsync(long appId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.ExecuteScalarAsync<long?>(
            new CommandDefinition(GetDefaultRoleIdSql, new { appId, tenantId = QueryContext.TenantId }, cancellationToken: ct));
    }

    public async Task<int> UpdateAsync(Guid publicId, string name, string? description, string? icon, string? color, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.ExecuteAsync(
            new CommandDefinition(UpdateSql, new
            {
                tenantId = QueryContext.TenantId,
                publicId,
                name,
                description,
                icon,
                color,
                modifiedBy = QueryContext.UserId,
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
