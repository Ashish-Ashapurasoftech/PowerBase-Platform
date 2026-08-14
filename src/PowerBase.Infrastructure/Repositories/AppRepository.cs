using Dapper;
using Microsoft.Extensions.Configuration;
using System.IO;
using System.Linq;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.ValueObjects;
using PowerBase.Application.Common.Models;
using PowerBase.Domain.Exceptions;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class AppRepository : TenantRepositoryBase, IAppRepository
{
    private const string SelectColumns = "Id, PublicId, OwnerId, OwnerName, Name, Description, Icon, Color, Status, Formatting, SecurityOptions, IsEncrypted, Branding, LayoutSettings, IsDeleted, CreatedOn, CreatedBy, ModifiedOn, ModifiedBy, DeletedOn, DeletedBy, RowVersion";

    private const string GetByPublicIdSql = $"""
        SELECT {SelectColumns}
        FROM meta.App
        WHERE PublicId = @publicId
          AND IsDeleted = 0
        """;

    private const string ListSql = $"""
        SELECT {SelectColumns}
        FROM meta.App
        WHERE IsDeleted = 0
        ORDER BY Name
        OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY
        """;

    private const string CountSql = """
        SELECT COUNT(1)
        FROM meta.App
        WHERE IsDeleted = 0
        """;

    private const string ListByUserSql = $"""
        SELECT a.Id, a.PublicId, a.OwnerId, a.OwnerName, a.Name, a.Description, a.Icon, a.Color,
               a.Status, a.Formatting, a.SecurityOptions, a.IsEncrypted, a.IsDeleted, a.CreatedOn, a.CreatedBy, a.ModifiedOn, a.ModifiedBy,
               a.DeletedOn, a.DeletedBy, a.RowVersion
        FROM meta.App a
        JOIN meta.AppUser au ON au.AppId = a.Id
        WHERE au.UserId   = @userId
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
        WHERE au.UserId   = @userId
          AND au.IsDeleted = 0
          AND a.IsDeleted  = 0
        """;

    private const string ListAllByUserSql = $"""
        SELECT a.Id, a.PublicId, a.OwnerId, a.OwnerName, a.Name, a.Description, a.Icon, a.Color,
               a.Status, a.Formatting, a.SecurityOptions, a.IsEncrypted, a.IsDeleted, a.CreatedOn, a.CreatedBy, a.ModifiedOn, a.ModifiedBy,
               a.DeletedOn, a.DeletedBy, a.RowVersion
        FROM meta.App a
        JOIN meta.AppUser au ON au.AppId = a.Id
        WHERE au.UserId   = @userId
          AND au.IsDeleted = 0
          AND a.IsDeleted  = 0
        ORDER BY a.Name
        """;

    private const string NameExistsSql = """
        SELECT CAST(CASE WHEN EXISTS (
            SELECT 1 FROM meta.App
            WHERE Name = @name AND IsDeleted = 0
        ) THEN 1 ELSE 0 END AS BIT)
        """;

    private const string GetIdByPublicIdSql = """
        SELECT Id FROM meta.App
        WHERE PublicId = @publicId AND IsDeleted = 0
        """;

    private const string GetPublicIdByIdSql = """
        SELECT PublicId FROM meta.App
        WHERE Id = @appId AND IsDeleted = 0
        """;

    private const string InsertSql = """
        INSERT INTO meta.App (OwnerId, OwnerName, Name, Description, Icon, Color, Status, Formatting, SecurityOptions, IsEncrypted, IsDeleted, CreatedOn, CreatedBy)
        OUTPUT INSERTED.PublicId, INSERTED.Id
        VALUES (@ownerId, @ownerName, @name, @description, @icon, @color, @status, @formatting, @securityOptions, @isEncrypted, 0, SYSUTCDATETIME(), @createdBy)
        """;

    private const string SetDefaultRoleSql = """
        UPDATE meta.App
        SET DefaultAppRoleId = @roleId
        WHERE Id = @appId AND IsDeleted = 0
        """;

    private const string GetDefaultRoleIdSql = """
        SELECT DefaultAppRoleId FROM meta.App
        WHERE Id = @appId AND IsDeleted = 0
        """;

    private const string UpdateSql = """
        UPDATE meta.App
        SET Name        = @name,
            Description = @description,
            Icon        = @icon,
            Color       = @color,
            Formatting  = @formatting,
            SecurityOptions = @securityOptions,
            IsEncrypted = @isEncrypted,
            ModifiedOn  = SYSUTCDATETIME(),
            ModifiedBy  = @modifiedBy
        WHERE PublicId  = @publicId
          AND IsDeleted = 0
        """;

    private const string UpdateBrandingSql = """
        UPDATE meta.App
        SET Branding       = @branding,
            LayoutSettings = @layoutSettings,
            ModifiedOn     = SYSUTCDATETIME(),
            ModifiedBy     = @modifiedBy
        WHERE PublicId  = @publicId
          AND IsDeleted = 0
        """;

    private const string SoftDeleteSql = """
        UPDATE meta.App
        SET IsDeleted = 1,
            ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy,
            DeletedOn  = SYSUTCDATETIME(), DeletedBy  = @modifiedBy
        WHERE PublicId = @publicId
          AND IsDeleted = 0
        """;

    private readonly IConfiguration _configuration;
    private readonly IEncryptionService _encryptionService;

    public AppRepository(ITenantConnectionFactory connectionFactory, IQueryContext queryContext, IConfiguration configuration, IEncryptionService encryptionService)
        : base(connectionFactory, queryContext)
    {
        _configuration = configuration;
        _encryptionService = encryptionService;
    }

    public async Task<App> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var app = await connection.QuerySingleOrDefaultAsync<App>(
            new CommandDefinition(GetByPublicIdSql, new { publicId }, cancellationToken: ct));
        return app ?? throw new NotFoundException("App", publicId);
    }

    public async Task<IReadOnlyList<App>> ListAsync(int page, int pageSize, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var results = await connection.QueryAsync<App>(
            new CommandDefinition(ListSql, new { offset = (page - 1) * pageSize, pageSize }, cancellationToken: ct));
        return results.AsList();
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(CountSql, cancellationToken: ct));
    }

    public async Task<bool> NameExistsAsync(string name, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(NameExistsSql, new { name }, cancellationToken: ct));
    }

    public async Task<long> GetIdByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var id = await connection.ExecuteScalarAsync<long?>(
            new CommandDefinition(GetIdByPublicIdSql, new { publicId }, cancellationToken: ct));
        return id ?? throw new NotFoundException("App", publicId);
    }

    public async Task<Guid> GetPublicIdByIdAsync(long appId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var publicId = await connection.ExecuteScalarAsync<Guid?>(
            new CommandDefinition(GetPublicIdByIdSql, new { appId }, cancellationToken: ct));
        return publicId ?? throw new NotFoundException("App", appId);
    }

    public async Task<(Guid PublicId, long Id)> CreateAsync(App app, System.Data.IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        var parameters = new
        {
            ownerId = app.OwnerId,
            ownerName = app.OwnerName,
            name = app.Name,
            description = app.Description,
            icon = app.Icon,
            color = app.Color,
            status = app.Status,
            formatting = app.Formatting,
            securityOptions = app.SecurityOptions,
            isEncrypted = app.IsEncrypted,
            createdBy = QueryContext.UserId,
        };

        // If IsEncrypted is enabled, but no DEK exists in SecurityOptions, generate one and store it.
        // We use string.Empty or JSON serialization in practice. For simplicity, we assume SecurityOptions holds the DEK.
        if (app.IsEncrypted && string.IsNullOrEmpty(app.SecurityOptions))
        {
            // We need a dummy AppId for DEK generation since the App isn't created yet,
            // but we can generate the DEK using publicId instead for the derivation info.
            // Or generate it in a transaction AFTER insert. Let's do it after insert below.
        }

        if (transaction is not null)
        {
            var row = await transaction.Connection!.QuerySingleAsync<(Guid PublicId, long Id)>(
                new CommandDefinition(InsertSql, parameters, transaction, cancellationToken: ct));
            
            if (app.IsEncrypted)
            {
                var security = string.IsNullOrEmpty(app.SecurityOptions) || !app.SecurityOptions.TrimStart().StartsWith("{")
                    ? new AppSecurityOptionsSettings()
                    : System.Text.Json.JsonSerializer.Deserialize<AppSecurityOptionsSettings>(app.SecurityOptions, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AppSecurityOptionsSettings();

                if (string.IsNullOrEmpty(security.WrappedDek))
                {
                    security.WrappedDek = await _encryptionService.GenerateAndWrapDekAsync(QueryContext.TenantId, row.Id, ct);
                    var serializedSecurity = System.Text.Json.JsonSerializer.Serialize(security);
                    await transaction.Connection!.ExecuteAsync("UPDATE meta.App SET SecurityOptions = @serializedSecurity WHERE Id = @Id", new { serializedSecurity, row.Id }, transaction);
                }
            }
            return row;
        }

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var insertedRow = await connection.QuerySingleAsync<(Guid PublicId, long Id)>(
            new CommandDefinition(InsertSql, parameters, cancellationToken: ct));

        if (app.IsEncrypted)
        {
            var security = string.IsNullOrEmpty(app.SecurityOptions) || !app.SecurityOptions.TrimStart().StartsWith("{")
                ? new AppSecurityOptionsSettings()
                : System.Text.Json.JsonSerializer.Deserialize<AppSecurityOptionsSettings>(app.SecurityOptions, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AppSecurityOptionsSettings();

            if (string.IsNullOrEmpty(security.WrappedDek))
            {
                security.WrappedDek = await _encryptionService.GenerateAndWrapDekAsync(QueryContext.TenantId, insertedRow.Id, ct);
                var serializedSecurity = System.Text.Json.JsonSerializer.Serialize(security);
                await connection.ExecuteAsync("UPDATE meta.App SET SecurityOptions = @serializedSecurity WHERE Id = @Id", new { serializedSecurity, Id = insertedRow.Id });
            }
        }

        return insertedRow;
    }

    public async Task<IReadOnlyList<AppListItemDto>> ListByUserAsync(long userId, int page, int pageSize, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var results = await connection.QueryAsync<AppListItemDto>(
            new CommandDefinition(ListByUserSql, new { userId, Status = "Active", offset = (page - 1) * pageSize, pageSize }, cancellationToken: ct));
        return results.AsList();
    }

    public async Task<int> CountByUserAsync(long userId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(CountByUserSql, new { userId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<App>> ListAllByUserAsync(long userId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var results = await connection.QueryAsync<App>(
            new CommandDefinition(ListAllByUserSql, new { userId }, cancellationToken: ct));
        return results.AsList();
    }

    public async Task SetDefaultRoleAsync(long appId, long roleId, System.Data.IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        var parameters = new { appId, roleId };
        if (transaction is not null)
        {
            await transaction.Connection!.ExecuteAsync(new CommandDefinition(SetDefaultRoleSql, parameters, transaction, cancellationToken: ct));
            return;
        }
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(SetDefaultRoleSql, parameters, cancellationToken: ct));
    }

    public async Task<long?> GetDefaultRoleIdAsync(long appId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<long?>(
            new CommandDefinition(GetDefaultRoleIdSql, new { appId }, cancellationToken: ct));
    }

    public async Task<int> UpdateAsync(Guid publicId, string name, string? description, string? icon, string? color, string? formatting, string? securityOptions, bool isEncrypted, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteAsync(
            new CommandDefinition(UpdateSql, new
            {
                publicId, name, description, icon, color, formatting, securityOptions, isEncrypted,
                modifiedBy = QueryContext.UserId,
            }, cancellationToken: ct));
    }

    public async Task<int> UpdateBrandingAsync(Guid publicId, string? branding, string? layoutSettings, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteAsync(
            new CommandDefinition(UpdateBrandingSql, new
            {
                publicId, branding, layoutSettings,
                modifiedBy = QueryContext.UserId,
            }, cancellationToken: ct));
    }

    public async Task<long> GetDatabaseSizeBytesAsync(CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<long>(
            "SELECT SUM(CAST(size AS BIGINT)) * 8192 FROM sys.database_files"
        );
    }

    public Task<long> GetFileStorageSizeBytesAsync(CancellationToken ct = default)
    {
        var localPath = _configuration["Storage:LocalPath"] ?? "C:\\PowerbaseUploads";
        long size = 0;
        try
        {
            if (Directory.Exists(localPath))
            {
                var dirInfo = new DirectoryInfo(localPath);
                size = dirInfo.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
        }
        catch
        {
            size = 0;
        }
        return Task.FromResult(size);
    }

    public async Task DeleteAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(SoftDeleteSql, new { publicId, modifiedBy = QueryContext.UserId }, cancellationToken: ct));
        if (affected == 0)
            throw new NotFoundException("App", publicId);
    }
}
