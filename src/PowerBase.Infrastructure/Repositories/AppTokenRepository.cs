using System.Data;
using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class AppTokenRepository : TenantRepositoryBase, IAppTokenRepository
{
    public AppTokenRepository(
        ITenantConnectionFactory tenantConnectionFactory,
        IQueryContext queryContext)
        : base(tenantConnectionFactory, queryContext)
    {
    }

    private const string GetAppIdByPublicIdSql = @"
        SELECT Id FROM meta.App WHERE PublicId = @appPublicId AND IsDeleted = 0;";

    private const string InsertTokenSql = @"
        INSERT INTO meta.AppToken (PublicId, TenantId, AppId, CreatedByUserId, TokenName, Description, TokenHash, TokenPrefix, IsActive, CreatedAt, IsDeleted)
        OUTPUT INSERTED.Id, INSERTED.PublicId, INSERTED.TenantId, INSERTED.AppId, INSERTED.CreatedByUserId, INSERTED.TokenName, INSERTED.Description, INSERTED.TokenHash, INSERTED.TokenPrefix, INSERTED.IsActive, INSERTED.CreatedAt, INSERTED.LastUsedAt, INSERTED.IsDeleted, INSERTED.RowVersion
        VALUES (@PublicId, @TenantId, @AppId, @CreatedByUserId, @TokenName, @Description, @TokenHash, @TokenPrefix, @IsActive, @CreatedAt, 0);";

    private const string GetByPublicIdSql = @"
        SELECT t.Id, t.PublicId, t.TenantId, t.AppId, t.CreatedByUserId, t.TokenName, t.Description, t.TokenHash, t.TokenPrefix, t.IsActive, t.CreatedAt, t.LastUsedAt, t.IsDeleted, t.RowVersion
        FROM meta.AppToken t
        INNER JOIN meta.App a ON a.Id = t.AppId
        WHERE t.PublicId = @publicId AND t.TenantId = @tenantId AND a.PublicId = @appPublicId AND t.IsDeleted = 0;";

    private const string UpdateStatusSql = @"
        UPDATE t
        SET t.IsActive = @isActive
        FROM meta.AppToken t
        INNER JOIN meta.App a ON a.Id = t.AppId
        WHERE t.PublicId = @publicId AND t.TenantId = @tenantId AND a.PublicId = @appPublicId AND t.IsDeleted = 0;";

    private const string DeleteSql = @"
        UPDATE t
        SET t.IsDeleted = 1
        FROM meta.AppToken t
        INNER JOIN meta.App a ON a.Id = t.AppId
        WHERE t.PublicId = @publicId AND t.TenantId = @tenantId AND a.PublicId = @appPublicId AND t.IsDeleted = 0;";

    private const string RotateSecretSql = @"
        UPDATE meta.AppToken
        SET TokenHash = @newTokenHash,
            TokenPrefix = @newTokenPrefix,
            CreatedAt = SYSUTCDATETIME()
        WHERE Id = @id AND IsDeleted = 0;";

    public async Task<AppToken> CreateAsync(AppToken appToken, CancellationToken ct)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);

        var createdToken = await connection.QuerySingleAsync<AppToken>(
            new CommandDefinition(InsertTokenSql, appToken, cancellationToken: ct));

        return createdToken;
    }

    public async Task<AppToken?> GetByPublicIdAsync(Guid publicId, long tenantId, Guid appPublicId, CancellationToken ct)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);

        return await connection.QueryFirstOrDefaultAsync<AppToken>(
            new CommandDefinition(GetByPublicIdSql, new { publicId, tenantId, appPublicId }, cancellationToken: ct));
    }

    public async Task<(IEnumerable<AppToken> Items, int TotalCount)> GetPagedAsync(long tenantId, Guid appPublicId, string? search, bool? isActive, int page, int pageSize, CancellationToken ct)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);

        var appId = await connection.ExecuteScalarAsync<long?>(
            new CommandDefinition(GetAppIdByPublicIdSql, new { appPublicId }, cancellationToken: ct));

        if (appId == null)
            return (Enumerable.Empty<AppToken>(), 0);

        var dynamicParams = new DynamicParameters();
        dynamicParams.Add("tenantId", tenantId);
        dynamicParams.Add("appId", appId.Value);
        dynamicParams.Add("offset", (page - 1) * pageSize);
        dynamicParams.Add("pageSize", pageSize);

        var whereClause = "WHERE t.TenantId = @tenantId AND t.AppId = @appId AND t.IsDeleted = 0";
        if (!string.IsNullOrWhiteSpace(search))
        {
            whereClause += " AND (t.TokenName LIKE @searchPattern OR t.Description LIKE @searchPattern)";
            dynamicParams.Add("searchPattern", $"%{search}%");
        }
        if (isActive.HasValue)
        {
            whereClause += " AND t.IsActive = @isActive";
            dynamicParams.Add("isActive", isActive.Value);
        }

        var countSql = $"SELECT COUNT(1) FROM meta.AppToken t {whereClause};";

        var selectSql = $@"
            SELECT t.Id, t.PublicId, t.TenantId, t.AppId, t.CreatedByUserId, t.TokenName, t.Description, t.TokenHash, t.TokenPrefix, t.IsActive, t.CreatedAt, t.LastUsedAt, t.IsDeleted, t.RowVersion
            FROM meta.AppToken t
            {whereClause}
            ORDER BY t.CreatedAt DESC
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;";

        var totalCount = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(countSql, dynamicParams, cancellationToken: ct));

        var items = await connection.QueryAsync<AppToken>(
            new CommandDefinition(selectSql, dynamicParams, cancellationToken: ct));

        return (items, totalCount);
    }

    public async Task<bool> UpdateStatusAsync(Guid publicId, long tenantId, Guid appPublicId, bool isActive, CancellationToken ct)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);

        var rows = await connection.ExecuteAsync(
            new CommandDefinition(UpdateStatusSql, new { publicId, tenantId, appPublicId, isActive }, cancellationToken: ct));

        return rows > 0;
    }

    public async Task<bool> DeleteAsync(Guid publicId, long tenantId, Guid appPublicId, CancellationToken ct)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);

        var rows = await connection.ExecuteAsync(
            new CommandDefinition(DeleteSql, new { publicId, tenantId, appPublicId }, cancellationToken: ct));

        return rows > 0;
    }

    public async Task<bool> RotateSecretAsync(long id, string newTokenHash, string newTokenPrefix, CancellationToken ct)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);

        var rows = await connection.ExecuteAsync(
            new CommandDefinition(RotateSecretSql, new { id, newTokenHash, newTokenPrefix }, cancellationToken: ct));

        return rows > 0;
    }
}
