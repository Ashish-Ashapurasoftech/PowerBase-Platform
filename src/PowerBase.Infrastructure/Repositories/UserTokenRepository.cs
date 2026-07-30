using System.Data;
using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class UserTokenRepository : ControlRepositoryBase, IUserTokenRepository
{
    private readonly ITenantConnectionFactory _tenantConnectionFactory;
    private readonly ITenantConnectionResolver _tenantConnectionResolver;

    public UserTokenRepository(
        IControlConnectionFactory connectionFactory, 
        ITenantConnectionFactory tenantConnectionFactory,
        ITenantConnectionResolver tenantConnectionResolver,
        IQueryContext queryContext)
        : base(connectionFactory, queryContext)
    {
        _tenantConnectionFactory = tenantConnectionFactory;
        _tenantConnectionResolver = tenantConnectionResolver;
    }

    private const string InsertTokenSql = @"
        INSERT INTO core.UserToken (PublicId, TenantId, UserId, TokenName, Description, TokenHash, TokenPrefix, IsActive, AccessAllApps, CreatedAt, IsDeleted)
        OUTPUT INSERTED.Id, INSERTED.PublicId, INSERTED.TenantId, INSERTED.UserId, INSERTED.TokenName, INSERTED.Description, INSERTED.TokenHash, INSERTED.TokenPrefix, INSERTED.IsActive, INSERTED.AccessAllApps, INSERTED.CreatedAt, INSERTED.LastUsedAt, INSERTED.IsDeleted, INSERTED.RowVersion
        VALUES (@PublicId, @TenantId, @UserId, @TokenName, @Description, @TokenHash, @TokenPrefix, @IsActive, @AccessAllApps, @CreatedAt, 0);";

    private const string InsertAppRestrictionSql = @"
        INSERT INTO core.UserTokenAppRestriction (UserTokenId, AppId, CreatedAt)
        VALUES (@userTokenId, @appId, SYSUTCDATETIME());";

    private const string GetAppIdByPublicIdSql = @"
        SELECT Id FROM meta.App WHERE PublicId = @appPublicId AND IsDeleted = 0;";

    private const string GetByPublicIdSql = @"
        SELECT Id, PublicId, TenantId, UserId, TokenName, Description, TokenHash, TokenPrefix, IsActive, AccessAllApps, CreatedAt, LastUsedAt, IsDeleted, RowVersion
        FROM core.UserToken
        WHERE PublicId = @publicId AND TenantId = @tenantId AND IsDeleted = 0;";

    private const string GetAllowedAppIdsSql = @"
        SELECT AppId FROM core.UserTokenAppRestriction WHERE UserTokenId = @userTokenId;";

    private const string GetAppPublicIdByIdSql = @"
        SELECT PublicId FROM meta.App WHERE Id = @appId AND IsDeleted = 0;";

    private const string GetMyTokensSql = @"
        SELECT Id, PublicId, TenantId, UserId, TokenName, Description, TokenHash, TokenPrefix, IsActive, AccessAllApps, CreatedAt, LastUsedAt, IsDeleted, RowVersion
        FROM core.UserToken
        WHERE UserId = @userId AND TenantId = @tenantId AND IsDeleted = 0
        ORDER BY CreatedAt DESC;";

    private const string UpdateStatusSql = @"
        UPDATE core.UserToken
        SET IsActive = @isActive
        WHERE PublicId IN @publicIds AND TenantId = @tenantId AND IsDeleted = 0;";

    private const string RevokeSql = @"
        UPDATE core.UserToken
        SET IsDeleted = 1
        WHERE PublicId = @publicId AND TenantId = @tenantId AND IsDeleted = 0;";

    public async Task<UserToken> CreateAsync(UserToken userToken, IEnumerable<Guid>? allowedAppPublicIds, CancellationToken ct)
    {
        await using var connection = ConnectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        try
        {
            var createdToken = await connection.QuerySingleAsync<UserToken>(
                new CommandDefinition(InsertTokenSql, userToken, transaction: transaction, cancellationToken: ct)
            );

            if (!userToken.AccessAllApps && allowedAppPublicIds != null && allowedAppPublicIds.Any())
            {
                await using var tenantConn = await _tenantConnectionFactory.CreateAsync(ct);
                
                foreach (var appPublicId in allowedAppPublicIds)
                {
                    var appId = await tenantConn.QuerySingleOrDefaultAsync<long?>(
                        new CommandDefinition(GetAppIdByPublicIdSql, new { appPublicId }, cancellationToken: ct)
                    );

                    if (appId.HasValue)
                    {
                        await connection.ExecuteAsync(
                            new CommandDefinition(InsertAppRestrictionSql, new { userTokenId = createdToken.Id, appId = appId.Value }, transaction: transaction, cancellationToken: ct)
                        );
                    }
                }
            }

            transaction.Commit();
            return createdToken;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private const string GetByPublicIdAdminSql = @"
        SELECT Id, PublicId, TenantId, UserId, TokenName, Description, TokenHash, TokenPrefix, IsActive, AccessAllApps, CreatedAt, LastUsedAt, IsDeleted, RowVersion
        FROM core.UserToken
        WHERE PublicId = @publicId AND IsDeleted = 0;";

    public async Task<UserToken?> GetByPublicIdAsync(Guid publicId, long tenantId, CancellationToken ct)
    {
        await using var connection = ConnectionFactory.Create();
        var sql = tenantId > 0 ? GetByPublicIdSql : GetByPublicIdAdminSql;
        return await connection.QuerySingleOrDefaultAsync<UserToken>(
            new CommandDefinition(sql, new { publicId, tenantId }, cancellationToken: ct)
        );
    }

    private const string GetAppDetailsByIdSql = @"
        SELECT PublicId, Name FROM meta.App WHERE Id = @appId AND IsDeleted = 0;";

    public async Task<(IEnumerable<Guid> PublicIds, IEnumerable<string> Names)> GetAllowedAppDetailsAsync(long userTokenId, long? targetTenantId = null, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var appIds = await connection.QueryAsync<long>(
            new CommandDefinition(GetAllowedAppIdsSql, new { userTokenId }, cancellationToken: ct)
        );

        if (!appIds.Any()) return (Enumerable.Empty<Guid>(), Enumerable.Empty<string>());

        var tenantIdToUse = (targetTenantId.HasValue && targetTenantId.Value > 0) 
            ? targetTenantId.Value 
            : QueryContext.TenantId;

        if (tenantIdToUse == 0)
        {
            return (Enumerable.Empty<Guid>(), Enumerable.Empty<string>());
        }

        var connStr = await _tenantConnectionResolver.ResolveAsync(tenantIdToUse, ct);
        await using var tenantConn = new Microsoft.Data.SqlClient.SqlConnection(connStr);
        var publicIds = new List<Guid>();
        var names = new List<string>();

        foreach (var appId in appIds)
        {
            var appDetail = await tenantConn.QuerySingleOrDefaultAsync<AppDetailDto>(
                new CommandDefinition(GetAppDetailsByIdSql, new { appId }, cancellationToken: ct)
            );
            if (appDetail != null)
            {
                publicIds.Add(appDetail.PublicId);
                if (!string.IsNullOrWhiteSpace(appDetail.Name))
                {
                    names.Add(appDetail.Name);
                }
            }
        }

        return (publicIds, names);
    }

    public async Task<IEnumerable<Guid>> GetAllowedAppPublicIdsAsync(long userTokenId, long? targetTenantId = null, CancellationToken ct = default)
    {
        var (publicIds, _) = await GetAllowedAppDetailsAsync(userTokenId, targetTenantId, ct);
        return publicIds;
    }

    public async Task<IEnumerable<UserToken>> GetMyTokensAsync(long userId, long tenantId, CancellationToken ct)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.QueryAsync<UserToken>(
            new CommandDefinition(GetMyTokensSql, new { userId, tenantId }, cancellationToken: ct)
        );
    }

    public async Task<(IEnumerable<UserToken> Items, int TotalCount)> GetAdminTokensPagedAsync(
        long tenantId, 
        string? search, 
        bool? isActive, 
        int page, 
        int pageSize, 
        CancellationToken ct)
    {
        await using var connection = ConnectionFactory.Create();

        var whereClause = tenantId > 0 
            ? "WHERE ut.TenantId = @tenantId AND ut.IsDeleted = 0" 
            : "WHERE ut.IsDeleted = 0";

        var parameters = new DynamicParameters();
        if (tenantId > 0)
        {
            parameters.Add("tenantId", tenantId);
        }
        parameters.Add("offset", (page - 1) * pageSize);
        parameters.Add("pageSize", pageSize);

        if (!string.IsNullOrWhiteSpace(search))
        {
            whereClause += " AND (ut.TokenName LIKE @search OR ut.Description LIKE @search)";
            parameters.Add("search", $"%{search}%");
        }

        if (isActive.HasValue)
        {
            whereClause += " AND ut.IsActive = @isActive";
            parameters.Add("isActive", isActive.Value);
        }

        var countSql = $"SELECT COUNT(1) FROM core.UserToken ut {whereClause};";
        var itemsSql = $@"
            SELECT ut.Id, ut.PublicId, ut.TenantId, ut.UserId, ut.TokenName, ut.Description, ut.TokenHash, ut.TokenPrefix, ut.IsActive, ut.AccessAllApps, ut.CreatedAt, ut.LastUsedAt, ut.IsDeleted, ut.RowVersion
            FROM core.UserToken ut
            {whereClause}
            ORDER BY ut.CreatedAt DESC
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;";

        var totalCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(countSql, parameters, cancellationToken: ct));
        var items = await connection.QueryAsync<UserToken>(new CommandDefinition(itemsSql, parameters, cancellationToken: ct));

        return (items, totalCount);
    }

    private const string GetExistingPublicIdsSql = @"
        SELECT PublicId
        FROM core.UserToken
        WHERE PublicId IN @publicIds AND TenantId = @tenantId AND IsDeleted = 0;";

    private const string GetExistingPublicIdsAdminSql = @"
        SELECT PublicId
        FROM core.UserToken
        WHERE PublicId IN @publicIds AND IsDeleted = 0;";

    public async Task<IEnumerable<Guid>> GetExistingPublicIdsAsync(IEnumerable<Guid> publicIds, long tenantId, CancellationToken ct)
    {
        if (publicIds == null || !publicIds.Any()) return Enumerable.Empty<Guid>();
        await using var connection = ConnectionFactory.Create();
        var sql = tenantId > 0 ? GetExistingPublicIdsSql : GetExistingPublicIdsAdminSql;
        return await connection.QueryAsync<Guid>(
            new CommandDefinition(sql, new { publicIds, tenantId }, cancellationToken: ct)
        );  
    }

    private const string UpdateStatusAdminSql = @"
        UPDATE core.UserToken
        SET IsActive = @isActive
        WHERE PublicId IN @publicIds AND IsDeleted = 0;";

    public async Task<bool> UpdateStatusAsync(IEnumerable<Guid> publicIds, long tenantId, bool isActive, CancellationToken ct)
    {
        await using var connection = ConnectionFactory.Create();
        var sql = tenantId > 0 ? UpdateStatusSql : UpdateStatusAdminSql;
        var rowsAffected = await connection.ExecuteAsync(
            new CommandDefinition(sql, new { publicIds, tenantId, isActive }, cancellationToken: ct)
        );
        return rowsAffected > 0;
    }

    private const string RotateSecretSql = @"
        UPDATE core.UserToken
        SET TokenHash = @newTokenHash, TokenPrefix = @newTokenPrefix
        WHERE Id = @id AND IsDeleted = 0;";

    private const string RevokeAdminSql = @"
        UPDATE core.UserToken
        SET IsDeleted = 1
        WHERE PublicId = @publicId AND IsDeleted = 0;";

    public async Task<bool> RevokeAsync(Guid publicId, long tenantId, CancellationToken ct)
    {
        await using var connection = ConnectionFactory.Create();
        var sql = tenantId > 0 ? RevokeSql : RevokeAdminSql;
        var rowsAffected = await connection.ExecuteAsync(
            new CommandDefinition(sql, new { publicId, tenantId }, cancellationToken: ct)
        );
        return rowsAffected > 0;
    }

    public async Task<bool> RotateSecretAsync(long id, string newTokenHash, string newTokenPrefix, CancellationToken ct)
    {
        await using var connection = ConnectionFactory.Create();
        var rowsAffected = await connection.ExecuteAsync(
            new CommandDefinition(RotateSecretSql, new { id, newTokenHash, newTokenPrefix }, cancellationToken: ct)
        );
        return rowsAffected > 0;
    }

    private class AppDetailDto
    {
        public Guid PublicId { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}

