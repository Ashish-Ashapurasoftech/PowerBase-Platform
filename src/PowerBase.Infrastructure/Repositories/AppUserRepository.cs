using System.Data;
using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class AppUserRepository : TenantRepositoryBase, IAppUserRepository
{
    private const string ListByAppIdSql = """
        SELECT
            au.PublicId,
            au.UserPublicId,
            au.UserName,
            au.UserEmail,
            ar.PublicId AS RolePublicId,
            ar.Name     AS RoleName,
            au.Status,
            ISNULL(au.ShowInUserPickers, 1) AS ShowInUserPickers,
            au.CreatedOn,
            CAST(IIF(a.OwnerId = au.UserId, 1, 0) AS BIT) AS IsOwner,
            ISNULL(au.IsFromGroup, 0) AS IsFromGroup
        FROM meta.AppUser au
        JOIN meta.AppRole ar ON ar.Id = au.AppRoleId
        JOIN meta.App a ON a.Id = au.AppId
        WHERE au.AppId    = @appId
          AND au.IsDeleted = 0
        ORDER BY au.UserName
        """;

    private const string ListForUserPickerSql = """
        SELECT
            au.PublicId,
            au.UserPublicId,
            au.UserName,
            au.UserEmail,
            ar.PublicId AS RolePublicId,
            ar.Name     AS RoleName,
            au.Status,
            ISNULL(au.ShowInUserPickers, 1) AS ShowInUserPickers,
            au.CreatedOn,
            CAST(IIF(a.OwnerId = au.UserId, 1, 0) AS BIT) AS IsOwner,
            ISNULL(au.IsFromGroup, 0) AS IsFromGroup
        FROM meta.AppUser au
        JOIN meta.AppRole ar ON ar.Id = au.AppRoleId
        JOIN meta.App a ON a.Id = au.AppId
        WHERE au.AppId    = @appId
          AND au.IsDeleted = 0
          AND au.Status    = 'Active'
          AND ISNULL(au.ShowInUserPickers, 1) = 1
        ORDER BY au.UserName
        """;

    private const string ListByAppPagedSqlTemplate = """
        SELECT
            au.PublicId,
            au.UserPublicId,
            au.UserName,
            au.UserEmail,
            ar.PublicId AS RolePublicId,
            ar.Name     AS RoleName,
            au.Status,
            ISNULL(au.ShowInUserPickers, 1) AS ShowInUserPickers,
            au.CreatedOn,
            CAST(IIF(a.OwnerId = au.UserId, 1, 0) AS BIT) AS IsOwner,
            ISNULL(au.IsFromGroup, 0) AS IsFromGroup
        FROM meta.AppUser au
        JOIN meta.AppRole ar ON ar.Id = au.AppRoleId
        JOIN meta.App a ON a.Id = au.AppId
        WHERE au.AppId    = @appId
          AND au.IsDeleted = 0
          AND (@search IS NULL OR au.UserName LIKE @search OR au.UserEmail LIKE @search)
          AND (@role IS NULL OR ar.Name = @role)
        ORDER BY {0}
        OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY
        """;

    private const string ListByAppFilteredSqlTemplate = """
        SELECT
            au.PublicId,
            au.UserPublicId,
            au.UserName,
            au.UserEmail,
            ar.PublicId AS RolePublicId,
            ar.Name     AS RoleName,
            au.Status,
            ISNULL(au.ShowInUserPickers, 1) AS ShowInUserPickers,
            au.CreatedOn,
            CAST(IIF(a.OwnerId = au.UserId, 1, 0) AS BIT) AS IsOwner,
            ISNULL(au.IsFromGroup, 0) AS IsFromGroup
        FROM meta.AppUser au
        JOIN meta.AppRole ar ON ar.Id = au.AppRoleId
        JOIN meta.App a ON a.Id = au.AppId
        WHERE au.AppId    = @appId
          AND au.IsDeleted = 0
          AND (@search IS NULL OR au.UserName LIKE @search OR au.UserEmail LIKE @search)
          AND (@role IS NULL OR ar.Name = @role)
        ORDER BY {0}
        """;

    private const string CountByAppSql = """
        SELECT COUNT(1)
        FROM meta.AppUser au
        JOIN meta.AppRole ar ON ar.Id = au.AppRoleId
        JOIN meta.App a ON a.Id = au.AppId
        WHERE au.AppId    = @appId
          AND au.IsDeleted = 0
          AND (@search IS NULL OR au.UserName LIKE @search OR au.UserEmail LIKE @search)
          AND (@role IS NULL OR ar.Name = @role)
        """;

    private const string GetByAppAndUserSql = """
        SELECT Id, PublicId, AppId, UserId, UserPublicId, AppRoleId, Status, ISNULL(ShowInUserPickers, 1) AS ShowInUserPickers, AddedBy, CreatedOn, UpdatedOn, IsDeleted, IsFromGroup, GroupId
        FROM meta.AppUser
        WHERE AppId = @appId AND UserId = @userId AND IsDeleted = 0 And Status = 'Active'
        """;

    private const string InsertSql = """
        IF EXISTS (SELECT 1 FROM meta.AppUser WHERE AppId = @appId AND UserId = @userId)
        BEGIN
            UPDATE meta.AppUser
            SET Status = 'Active',
                IsDeleted = 0,
                AppRoleId = @appRoleId,
                IsFromGroup = @isFromGroup,
                GroupId = @groupId,
                UpdatedOn = SYSUTCDATETIME()
            WHERE AppId = @appId AND UserId = @userId
        END
        ELSE
        BEGIN
            INSERT INTO meta.AppUser (AppId, UserId, UserPublicId, UserName, UserEmail, AppRoleId, Status, ShowInUserPickers, AddedBy, CreatedOn, IsFromGroup, GroupId)
            VALUES (@appId, @userId, @userPublicId, @userName, @userEmail, @appRoleId, @status, 1, @addedBy, SYSUTCDATETIME(), @isFromGroup, @groupId)
        END
        """;

    private const string UpdateRoleSql = """
        UPDATE meta.AppUser
        SET AppRoleId = @appRoleId, UpdatedOn = SYSUTCDATETIME()
        WHERE AppId = @appId AND UserId = @userId AND IsDeleted = 0
        """;

    private const string UpdateShowInUserPickersSql = """
        UPDATE meta.AppUser
        SET ShowInUserPickers = @showInUserPickers, UpdatedOn = SYSUTCDATETIME()
        WHERE AppId = @appId AND UserId = @userId AND IsDeleted = 0
        """;

    private const string GetUserRoleNameSql = """
        SELECT ar.Name
        FROM meta.AppUser au
        JOIN meta.AppRole ar ON ar.Id = au.AppRoleId
        WHERE au.AppId = @appId AND au.UserId = @userId AND au.IsDeleted = 0
        """;

    private const string GetUserRolePublicIdSql = """
        SELECT ar.PublicId
        FROM meta.AppUser au
        JOIN meta.AppRole ar ON ar.Id = au.AppRoleId
        WHERE au.AppId = @appId AND au.UserId = @userId AND au.IsDeleted = 0
        """;

    private const string GetPermissionFlagsSql = """
        SELECT ar.CanViewRecords, ar.CanAddRecords, ar.CanEditRecords, ar.CanDeleteRecords
        FROM meta.AppUser au
        JOIN meta.AppRole ar ON ar.Id = au.AppRoleId
        JOIN meta.AppRolePermission arp ON arp.AppRoleId = ar.Id
        JOIN meta.Permission p ON p.Id = arp.PermissionId
        WHERE au.AppId = @appId AND au.UserId = @userId AND au.IsDeleted = 0
        """;

    private const string GetAppPermissionsSql = """
        SELECT p.Code
        FROM meta.AppUser au
        JOIN meta.AppRole ar ON ar.Id = au.AppRoleId
        JOIN meta.AppRolePermission arp ON arp.AppRoleId = ar.Id
        JOIN meta.Permission p ON p.Id = arp.PermissionId
        WHERE au.AppId = @appId AND au.UserId = @userId AND au.IsDeleted = 0
        
        UNION
        
        SELECT p.Code
        FROM meta.GroupMember gm
        JOIN meta.[Group] g ON g.Id = gm.GroupId
        JOIN meta.GroupApp ga ON ga.GroupId = g.Id
        JOIN meta.AppRole ar ON ar.Id = ga.AppRoleId
        JOIN meta.AppRolePermission arp ON arp.AppRoleId = ar.Id
        JOIN meta.Permission p ON p.Id = arp.PermissionId
        WHERE ga.AppId = @appId 
          AND gm.UserId = @userId
          AND gm.IsDeleted = 0 AND g.IsDeleted = 0 AND ga.IsDeleted = 0
        """;

    private const string GetUserAppRoleIdsSql = """
        SELECT au.AppRoleId
        FROM meta.AppUser au
        WHERE au.AppId = @appId AND au.UserId = @userId AND au.IsDeleted = 0

        UNION

        SELECT ga.AppRoleId
        FROM meta.GroupMember gm
        JOIN meta.[Group] g ON g.Id = gm.GroupId
        JOIN meta.GroupApp ga ON ga.GroupId = g.Id
        WHERE ga.AppId = @appId 
          AND gm.UserId = @userId
          AND gm.IsDeleted = 0 AND g.IsDeleted = 0 AND ga.IsDeleted = 0
        """;

    private const string RemoveSql = """
        UPDATE meta.AppUser
        SET IsDeleted = 1, Status = 'InActive', UpdatedOn = SYSUTCDATETIME()
        WHERE AppId = @appId AND UserId = @userId AND IsDeleted = 0
        """;

    public AppUserRepository(ITenantConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    public async Task<IReadOnlyList<AppUserDetail>> ListByAppIdAsync(long appId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var results = await connection.QueryAsync<AppUserDetail>(
            new CommandDefinition(ListByAppIdSql, new { appId }, cancellationToken: ct));
        return results.AsList();
    }

    public async Task<IReadOnlyList<AppUserDetail>> ListForUserPickerAsync(long appId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var results = await connection.QueryAsync<AppUserDetail>(
            new CommandDefinition(ListForUserPickerSql, new { appId }, cancellationToken: ct));
        return results.AsList();
    }

    public async Task<IReadOnlyList<AppUserDetail>> ListByAppPagedAsync(
        long appId,
        int page,
        int pageSize,
        string? search,
        string? role,
        string sortBy,
        bool sortDesc,
        CancellationToken ct = default)
    {
        var column = sortBy.ToLower() switch
        {
            "useremail" => "au.UserEmail",
            "accessvia" => "au.IsFromGroup",
            "rolename"  => "ar.Name",
            "addedon"   => "au.CreatedOn",
            _           => "au.UserName",
        };
        var sql = string.Format(ListByAppPagedSqlTemplate, $"{column} {(sortDesc ? "DESC" : "ASC")}");

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var rows = await connection.QueryAsync<AppUserDetail>(
            new CommandDefinition(sql, new
            {
                appId,
                search = string.IsNullOrWhiteSpace(search) ? null : $"%{search.Trim()}%",
                role = string.IsNullOrWhiteSpace(role) ? null : role,
                offset = (page - 1) * pageSize,
                pageSize
            }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<AppUserDetail>> ListByAppFilteredAsync(
        long appId,
        string? search,
        string? role,
        string sortBy,
        bool sortDesc,
        CancellationToken ct = default)
    {
        var column = sortBy.ToLower() switch
        {
            "useremail" => "au.UserEmail",
            "accessvia" => "au.IsFromGroup",
            "rolename"  => "ar.Name",
            "addedon"   => "au.CreatedOn",
            _           => "au.UserName",
        };
        var sql = string.Format(ListByAppFilteredSqlTemplate, $"{column} {(sortDesc ? "DESC" : "ASC")}");

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var rows = await connection.QueryAsync<AppUserDetail>(
            new CommandDefinition(sql, new
            {
                appId,
                search = string.IsNullOrWhiteSpace(search) ? null : $"%{search.Trim()}%",
                role = string.IsNullOrWhiteSpace(role) ? null : role
            }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<int> CountByAppAsync(
        long appId,
        string? search,
        string? role,
        CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(CountByAppSql, new
            {
                appId,
                search = string.IsNullOrWhiteSpace(search) ? null : $"%{search.Trim()}%",
                role = string.IsNullOrWhiteSpace(role) ? null : role,
            }, cancellationToken: ct));
    }

    public async Task<AppUser?> GetByAppAndUserAsync(long appId, long userId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<AppUser>(
            new CommandDefinition(GetByAppAndUserSql, new { appId, userId }, cancellationToken: ct));
    }

    public async Task CreateAsync(AppUser appUser, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        var parameters = new
        {
            appId      = appUser.AppId,
            userId     = appUser.UserId,
            userPublicId = appUser.UserPublicId,
            userName   = appUser.UserName,
            userEmail  = appUser.UserEmail,
            appRoleId  = appUser.AppRoleId,
            status     = appUser.Status,
            addedBy    = QueryContext.UserId,
            isFromGroup = appUser.IsFromGroup,
            groupId    = appUser.GroupId
        };

        if (transaction is not null)
        {
            await transaction.Connection!.ExecuteAsync(
                new CommandDefinition(InsertSql, parameters, transaction, cancellationToken: ct));
            return;
        }

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(InsertSql, parameters, cancellationToken: ct));
    }

    public async Task UpdateRoleAsync(long appId, long userId, long newRoleId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition(UpdateRoleSql, new { appId, userId, appRoleId = newRoleId }, cancellationToken: ct));
    }

    public async Task UpdateShowInUserPickersAsync(long appId, long userId, bool showInUserPickers, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition(UpdateShowInUserPickersSql, new { appId, userId, showInUserPickers }, cancellationToken: ct));
    }

    public async Task<string?> GetUserRoleNameAsync(long appId, long userId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<string?>(
            new CommandDefinition(GetUserRoleNameSql, new { appId, userId }, cancellationToken: ct));
    }

    public async Task<Guid?> GetUserRolePublicIdAsync(long appId, long userId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<Guid?>(
            new CommandDefinition(GetUserRolePublicIdSql, new { appId, userId }, cancellationToken: ct));
    }

    public async Task<IReadOnlySet<string>> GetUserAppPermissionsAsync(long appId, long userId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var result = await connection.QueryAsync<string>(
            new CommandDefinition(GetAppPermissionsSql, new { appId, userId }, cancellationToken: ct));
        return result.ToHashSet();
    }

    public async Task RemoveAsync(long appId, long userId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition(RemoveSql, new { appId, userId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<long>> GetUserAppRoleIdsAsync(long appId, long userId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var result = await connection.QueryAsync<long>(
            new CommandDefinition(GetUserAppRoleIdsSql, new { appId, userId }, cancellationToken: ct));
        return result.ToList();
    }

    public async Task<PowerBase.Application.Groups.Queries.GetUserEffectivePermissions.UserEffectivePermissionsDto> GetUserEffectivePermissionsAsync(Guid userPublicId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);

        const string GetAppUsersSql = @"
            SELECT au.Id AS AppUserId, au.AppId, a.PublicId AS AppPublicId, a.Name AS AppName, 
                   au.UserName, au.UserEmail, au.UserPublicId,
                   au.AppRoleId AS DirectRoleId, ar.PublicId AS DirectRolePublicId, ar.Name AS DirectRoleName
              FROM meta.AppUser au
              INNER JOIN meta.App a ON a.Id = au.AppId
              LEFT JOIN meta.AppRole ar ON ar.Id = au.AppRoleId
              WHERE au.UserPublicId = @userPublicId AND au.IsDeleted = 0 AND a.IsDeleted = 0
              
            UNION
            
            SELECT NULL AS AppUserId, a.Id AS AppId, a.PublicId AS AppPublicId, a.Name AS AppName,
                   (SELECT TOP 1 UserName FROM meta.AppUser WHERE UserPublicId = @userPublicId AND IsDeleted = 0) AS UserName,
                   (SELECT TOP 1 UserEmail FROM meta.AppUser WHERE UserPublicId = @userPublicId AND IsDeleted = 0) AS UserEmail,
                   @userPublicId AS UserPublicId,
                   NULL AS DirectRoleId, NULL AS DirectRolePublicId, NULL AS DirectRoleName
              FROM meta.GroupMember gm
              INNER JOIN meta.[Group] g ON g.Id = gm.GroupId
              INNER JOIN meta.GroupApp ga ON ga.GroupId = g.Id
              INNER JOIN meta.App a ON a.Id = ga.AppId
              CROSS APPLY (
                  SELECT TOP 1 au.UserId 
                  FROM meta.AppUser au 
                  WHERE au.UserPublicId = @userPublicId AND au.IsDeleted = 0
              ) u
              WHERE gm.UserId = u.UserId
                AND gm.IsDeleted = 0 AND g.IsDeleted = 0 AND ga.IsDeleted = 0 AND a.IsDeleted = 0
                AND NOT EXISTS (
                    SELECT 1 FROM meta.AppUser au2 
                    WHERE au2.AppId = a.Id AND au2.UserPublicId = @userPublicId AND au2.IsDeleted = 0
                );";

        var appUsers = (await conn.QueryAsync<dynamic>(GetAppUsersSql, new { userPublicId })).ToList();
        if (appUsers.Count == 0)
        {
            return new PowerBase.Application.Groups.Queries.GetUserEffectivePermissions.UserEffectivePermissionsDto
            {
                UserPublicId = userPublicId,
                Apps = new()
            };
        }

        var userName = appUsers[0].UserName;
        var userEmail = appUsers[0].UserEmail;

        const string GetInheritedRolesSql = @"
            SELECT gm.UserId, ga.AppId, g.PublicId AS GroupPublicId, g.Name AS GroupName, 
                   ar.PublicId AS AppRolePublicId, ar.Name AS AppRoleName
              FROM meta.GroupMember gm
              INNER JOIN meta.[Group] g ON g.Id = gm.GroupId
              INNER JOIN meta.GroupApp ga ON ga.GroupId = g.Id
              INNER JOIN meta.AppRole ar ON ar.Id = ga.AppRoleId
              CROSS APPLY (
                  SELECT TOP 1 au.UserId 
                  FROM meta.AppUser au 
                  WHERE au.UserPublicId = @userPublicId AND au.IsDeleted = 0
              ) u
              WHERE gm.UserId = u.UserId 
                AND gm.IsDeleted = 0 AND g.IsDeleted = 0 AND ga.IsDeleted = 0;";

        var inheritedRoles = (await conn.QueryAsync<dynamic>(GetInheritedRolesSql, new { userPublicId })).ToList();

        var resultApps = new List<PowerBase.Application.Groups.Queries.GetUserEffectivePermissions.AppPermissionDetailDto>();
        foreach (var au in appUsers)
        {
            var appId = (long)au.AppId;

            var appInherited = inheritedRoles
                .Where(r => (long)r.AppId == appId)
                .Select(r => new PowerBase.Application.Groups.Queries.GetUserEffectivePermissions.InheritedRoleDto
                {
                    GroupPublicId = r.GroupPublicId,
                    GroupName = r.GroupName,
                    AppRolePublicId = r.AppRolePublicId,
                    AppRoleName = r.AppRoleName
                }).ToList();

            const string GetPermissionsSql = @"
                SELECT p.Code
                FROM meta.AppUser au
                JOIN meta.AppRole ar ON ar.Id = au.AppRoleId
                JOIN meta.AppRolePermission arp ON arp.AppRoleId = ar.Id
                JOIN meta.Permission p ON p.Id = arp.PermissionId
                WHERE au.AppId = @appId AND au.UserPublicId = @userPublicId AND au.IsDeleted = 0
                
                UNION
                
                SELECT p.Code
                FROM meta.GroupMember gm
                JOIN meta.[Group] g ON g.Id = gm.GroupId
                JOIN meta.GroupApp ga ON ga.GroupId = g.Id
                JOIN meta.AppRole ar ON ar.Id = ga.AppRoleId
                JOIN meta.AppRolePermission arp ON arp.AppRoleId = ar.Id
                JOIN meta.Permission p ON p.Id = arp.PermissionId
                CROSS APPLY (
                    SELECT TOP 1 au.UserId 
                    FROM meta.AppUser au 
                    WHERE au.UserPublicId = @userPublicId AND au.IsDeleted = 0
                ) u
                WHERE ga.AppId = @appId 
                  AND gm.UserId = u.UserId 
                  AND gm.IsDeleted = 0 
                  AND g.IsDeleted = 0 
                  AND ga.IsDeleted = 0;";

            var permissions = (await conn.QueryAsync<string>(GetPermissionsSql, new { appId, userPublicId })).ToList();

            resultApps.Add(new PowerBase.Application.Groups.Queries.GetUserEffectivePermissions.AppPermissionDetailDto
            {
                AppPublicId = au.AppPublicId,
                AppName = au.AppName,
                DirectRoleName = au.DirectRoleName,
                InheritedRoles = appInherited,
                ConsolidatedPermissions = permissions
            });
        }

        return new PowerBase.Application.Groups.Queries.GetUserEffectivePermissions.UserEffectivePermissionsDto
        {
            UserPublicId = userPublicId,
            UserName = userName,
            UserEmail = userEmail,
            Apps = resultApps
        };
    }
}
