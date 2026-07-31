using System.Data;
using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.UserTokens.Common;
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

    private const string GetAdminTokensCountSql = @"
        SELECT COUNT(1) 
        FROM core.UserToken ut";

    private const string GetAdminTokensItemsSql = @"
        SELECT ut.Id, ut.PublicId, ut.TenantId, ut.UserId, ut.TokenName, ut.Description, ut.TokenHash, ut.TokenPrefix, ut.IsActive, ut.AccessAllApps, ut.CreatedAt, ut.LastUsedAt, ut.IsDeleted, ut.RowVersion,
               u.Name AS OwnerName, u.Email AS OwnerEmail
        FROM core.UserToken ut
        LEFT JOIN core.[User] u ON u.Id = ut.UserId";

    private const string GetAllowedAppIdsBatchSql = @"
        SELECT UserTokenId, AppId 
        FROM core.UserTokenAppRestriction 
        WHERE UserTokenId IN @restrictedIds";

    private const string GetAppDetailsByIdsSql = @"
        SELECT Id, PublicId, Name 
        FROM meta.App 
        WHERE Id IN @distinctAppIds AND IsDeleted = 0";

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

    public async Task<(IEnumerable<AdminUserTokenDto> Items, int TotalCount)> GetAdminTokensPagedAsync(
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

        var countSql = $"{GetAdminTokensCountSql} {whereClause};";
        var itemsSql = $@"{GetAdminTokensItemsSql}
            {whereClause}
            ORDER BY ut.CreatedAt DESC
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;";

        var totalCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(countSql, parameters, cancellationToken: ct));
        var dbRows = (await connection.QueryAsync<AdminUserTokenDbRow>(new CommandDefinition(itemsSql, parameters, cancellationToken: ct))).ToList();

        if (dbRows.Count == 0)
        {
            return (Enumerable.Empty<AdminUserTokenDto>(), totalCount);
        }

        // Mask function helper (matching original logic)
        string MaskTokenPrefix(string prefix)
        {
            var cleanPrefix = prefix.Replace("...", "");
            if (cleanPrefix.Length >= 8)
            {
                var first4 = cleanPrefix.Substring(0, 4);
                var last4 = cleanPrefix.Substring(cleanPrefix.Length - 4, 4);
                return $"{first4}************{last4}";
            }
            return $"{prefix}************";
        }

        // Batch fetch allowed apps for restricted tokens
        var restrictedRows = dbRows.Where(r => !r.AccessAllApps).ToList();
        var allowedAppsMap = new Dictionary<long, (IEnumerable<Guid> PublicIds, IEnumerable<string> Names)>();

        if (restrictedRows.Count > 0)
        {
            var restrictedIds = restrictedRows.Select(r => r.Id).Distinct().ToList();
            
            var restrictions = (await connection.QueryAsync<(long UserTokenId, long AppId)>(
                new CommandDefinition(GetAllowedAppIdsBatchSql, new { restrictedIds }, cancellationToken: ct)
            )).ToList();

            if (restrictions.Count > 0)
            {
                var tokenTenantMap = dbRows.ToDictionary(r => r.Id, r => r.TenantId);

                var restrictionsWithTenant = restrictions
                    .Select(r => new { r.UserTokenId, r.AppId, TenantId = tokenTenantMap.TryGetValue(r.UserTokenId, out var tId) ? tId : 0 })
                    .Where(x => x.TenantId > 0)
                    .GroupBy(x => x.TenantId);

                foreach (var tenantGroup in restrictionsWithTenant)
                {
                    var currentTenantId = tenantGroup.Key;
                    var distinctAppIds = tenantGroup.Select(x => x.AppId).Distinct().ToList();

                    try
                    {
                        var connStr = await _tenantConnectionResolver.ResolveAsync(currentTenantId, ct);
                        await using var tenantConn = new Microsoft.Data.SqlClient.SqlConnection(connStr);

                        var appDetails = (await tenantConn.QueryAsync<(long Id, Guid PublicId, string Name)>(
                            new CommandDefinition(GetAppDetailsByIdsSql, new { distinctAppIds }, cancellationToken: ct)
                        )).ToDictionary(a => a.Id, a => (a.PublicId, a.Name));

                        var groupedByToken = tenantGroup.GroupBy(x => x.UserTokenId);

                        foreach (var tokenGroup in groupedByToken)
                        {
                            var userTokenId = tokenGroup.Key;
                            var publicIds = new List<Guid>();
                            var names = new List<string>();

                            foreach (var item in tokenGroup)
                            {
                                if (appDetails.TryGetValue(item.AppId, out var appInfo))
                                {
                                    publicIds.Add(appInfo.PublicId);
                                    if (!string.IsNullOrWhiteSpace(appInfo.Name))
                                    {
                                        names.Add(appInfo.Name);
                                    }
                                }
                            }
                            allowedAppsMap[userTokenId] = (publicIds, names);
                        }
                    }
                    catch
                    {
                        // Ignore individual tenant failure to allow other tenants to process successfully
                    }
                }
            }
        }

        var dtoList = new List<AdminUserTokenDto>();
        foreach (var row in dbRows)
        {
            var (allowedApps, allowedAppNames) = row.AccessAllApps 
                ? (Enumerable.Empty<Guid>(), Enumerable.Empty<string>())
                : allowedAppsMap.TryGetValue(row.Id, out var appDetails) 
                    ? appDetails 
                    : (Enumerable.Empty<Guid>(), Enumerable.Empty<string>());

            dtoList.Add(new AdminUserTokenDto
            {
                PublicId = row.PublicId,
                TokenName = row.TokenName,
                Description = row.Description,
                TokenPrefix = MaskTokenPrefix(row.TokenPrefix),
                IsActive = row.IsActive,
                AccessAllApps = row.AccessAllApps,
                CreatedAt = row.CreatedAt,
                LastUsedAt = row.LastUsedAt,
                AllowedAppPublicIds = allowedApps,
                AllowedAppNames = allowedAppNames,
                UserId = row.UserId,
                OwnerName = row.OwnerName ?? string.Empty,
                OwnerEmail = row.OwnerEmail ?? string.Empty
            });
        }

        return (dtoList, totalCount);
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

    private const string GetByHashSql = @"
        SELECT Id, PublicId, TenantId, UserId, TokenName, Description, TokenHash, TokenPrefix, IsActive, AccessAllApps, CreatedAt, LastUsedAt, IsDeleted, RowVersion
        FROM core.UserToken
        WHERE TokenHash = @hash AND IsActive = 1 AND IsDeleted = 0;";

    private const string UpdateLastUsedAtSql = @"
        UPDATE core.UserToken
        SET LastUsedAt = SYSUTCDATETIME()
        WHERE Id = @id;";

    public async Task<UserToken?> GetByHashAsync(string hash, CancellationToken ct)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<UserToken>(
            new CommandDefinition(GetByHashSql, new { hash }, cancellationToken: ct)
        );
    }

    public async Task UpdateLastUsedAtAsync(long id, CancellationToken ct)
    {
        await using var connection = ConnectionFactory.Create();
        await connection.ExecuteAsync(
            new CommandDefinition(UpdateLastUsedAtSql, new { id }, cancellationToken: ct)
        );
    }

    private class AppDetailDto
    {
        public Guid PublicId { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private class AdminUserTokenDbRow
    {
        public long Id { get; set; }
        public Guid PublicId { get; set; }
        public long TenantId { get; set; }
        public long UserId { get; set; }
        public string TokenName { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string TokenPrefix { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public bool AccessAllApps { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastUsedAt { get; set; }
        public string OwnerName { get; set; } = string.Empty;
        public string OwnerEmail { get; set; } = string.Empty;
    }
}

