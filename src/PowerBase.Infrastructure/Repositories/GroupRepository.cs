using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Groups.Common;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class GroupRepository : TenantRepositoryBase, IGroupRepository
{
    private readonly IControlConnectionFactory _controlFactory;

    public GroupRepository(
        ITenantConnectionFactory connectionFactory,
        IQueryContext queryContext,
        IControlConnectionFactory controlFactory)
        : base(connectionFactory, queryContext)
    {
        _controlFactory = controlFactory;
    }

    // ── CREATE ───────────────────────────────────────────────────────────────

    private const string InsertGroupSql = @"
        INSERT INTO meta.[Group] (PublicId, Name, Description, CreatedOn, CreatedBy)
        OUTPUT INSERTED.Id, INSERTED.PublicId, INSERTED.Name, INSERTED.Description,
               INSERTED.CreatedOn, INSERTED.CreatedBy, INSERTED.IsDeleted, INSERTED.RowVersion
        VALUES (@PublicId, @Name, @Description, @CreatedOn, @CreatedBy);";

    public async Task<Group> CreateAsync(Group group, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<Group>(new CommandDefinition(InsertGroupSql, group, cancellationToken: ct));
    }

    // ── READ ─────────────────────────────────────────────────────────────────

    private const string GetByPublicIdSql = @"
        SELECT g.Id, g.PublicId, g.Name, g.Description, g.CreatedOn,
               (SELECT COUNT(1) FROM meta.GroupMember gm WHERE gm.GroupId = g.Id AND gm.IsDeleted = 0) AS MemberCount
        FROM meta.[Group] g
        WHERE g.PublicId = @publicId AND g.IsDeleted = 0;";

    public async Task<GroupDto?> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<GroupDto>(new CommandDefinition(
            GetByPublicIdSql, new { publicId }, cancellationToken: ct));
    }

    private const string ListPagedSql = @"
        SELECT g.Id, g.PublicId, g.Name, g.Description, g.CreatedOn,
               (SELECT COUNT(1) FROM meta.GroupMember gm WHERE gm.GroupId = g.Id AND gm.IsDeleted = 0) AS MemberCount
        FROM meta.[Group] g
        WHERE g.IsDeleted = 0
          AND (@search IS NULL OR g.Name LIKE '%' + @search + '%' OR g.Description LIKE '%' + @search + '%')
        ORDER BY g.Name
        OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;

        SELECT COUNT(1) FROM meta.[Group] g
        WHERE g.IsDeleted = 0
          AND (@search IS NULL OR g.Name LIKE '%' + @search + '%' OR g.Description LIKE '%' + @search + '%');";

    public async Task<(IEnumerable<GroupDto> Items, int TotalCount)> ListPagedAsync(
        string? search, int page, int pageSize, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var offset = (page - 1) * pageSize;
        await using var multi = await conn.QueryMultipleAsync(new CommandDefinition(
            ListPagedSql, new { search = string.IsNullOrWhiteSpace(search) ? null : search, offset, pageSize },
            cancellationToken: ct));

        var items = (await multi.ReadAsync<GroupDto>()).ToList();
        var total = await multi.ReadSingleAsync<int>();

        return (items, total);
    }

    // ── UPDATE ────────────────────────────────────────────────────────────────

    private const string UpdateGroupSql = @"
        UPDATE meta.[Group]
        SET Name = @name, Description = @description, ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy
        WHERE PublicId = @publicId AND IsDeleted = 0;
        SELECT @@ROWCOUNT;";

    public async Task<bool> UpdateAsync(Guid publicId, string name, string? description, long modifiedBy, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            UpdateGroupSql, new { publicId, name, description, modifiedBy }, cancellationToken: ct));
        return rows > 0;
    }

    // ── DELETE ────────────────────────────────────────────────────────────────

    private const string DeleteGroupSql = @"
        UPDATE meta.[Group]
        SET IsDeleted = 1, DeletedOn = SYSUTCDATETIME(), DeletedBy = @deletedBy
        WHERE PublicId = @publicId AND IsDeleted = 0;
        SELECT @@ROWCOUNT;";

    public async Task<bool> DeleteAsync(Guid publicId, long deletedBy, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var groupId = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT Id FROM meta.[Group] WHERE PublicId = @publicId AND IsDeleted = 0",
            new { publicId }, cancellationToken: ct));
            
        if (groupId is null) return false;
        
        var sharedAppIds = (await conn.QueryAsync<long>(new CommandDefinition(
            "SELECT AppId FROM meta.GroupApp WHERE GroupId = @groupId AND IsDeleted = 0",
            new { groupId = groupId.Value }, cancellationToken: ct))).ToList();
            
        foreach (var appId in sharedAppIds)
        {
            await SyncAppUsersAfterUnshareAsync(groupId.Value, appId, ct);
        }
        
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE meta.GroupApp SET IsDeleted = 1 WHERE GroupId = @groupId AND IsDeleted = 0",
            new { groupId = groupId.Value }, cancellationToken: ct));

        var rows = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            DeleteGroupSql, new { publicId, deletedBy }, cancellationToken: ct));
        return rows > 0;
    }

    // ── EXISTS BY NAME ────────────────────────────────────────────────────────

    private const string ExistsByNameSql = @"
        SELECT COUNT(1) FROM meta.[Group]
        WHERE Name = @name AND IsDeleted = 0
          AND (@excludePublicId IS NULL OR PublicId <> @excludePublicId);";

    public async Task<bool> ExistsByNameAsync(string name, Guid? excludePublicId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var count = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            ExistsByNameSql, new { name, excludePublicId }, cancellationToken: ct));
        return count > 0;
    }

    // ── MEMBERS ───────────────────────────────────────────────────────────────

    private const string InsertMemberSql = @"
        IF NOT EXISTS (SELECT 1 FROM meta.GroupMember WHERE GroupId = @groupId AND UserId = @userId AND IsDeleted = 0)
        BEGIN
            IF EXISTS (SELECT 1 FROM meta.GroupMember WHERE GroupId = @groupId AND UserId = @userId AND IsDeleted = 1)
                UPDATE meta.GroupMember SET IsDeleted = 0, CreatedOn = SYSUTCDATETIME(), AddedBy = @addedBy
                WHERE GroupId = @groupId AND UserId = @userId;
            ELSE
                INSERT INTO meta.GroupMember (GroupId, UserId, AddedBy, CreatedOn, IsDeleted)
                VALUES (@groupId, @userId, @addedBy, SYSUTCDATETIME(), 0);
            SELECT 1;
        END
        ELSE
            SELECT 0;";

    public async Task<int> AddMembersAsync(Guid groupPublicId, IEnumerable<Guid> userPublicIds, long addedBy, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var groupId = await conn.ExecuteScalarAsync<long?>(
            "SELECT Id FROM meta.[Group] WHERE PublicId = @groupPublicId AND IsDeleted = 0", new { groupPublicId });
        if (groupId is null) return 0;

        // Resolve UserPublicId → UserId via control DB (core.User is in shared/control database)
        var tenantId = QueryContext.TenantId;
        await using var ctrlConn = _controlFactory.Create();
        await ctrlConn.OpenAsync(ct);
        var userIds = (await ctrlConn.QueryAsync<long>(
            @"SELECT DISTINCT u.Id
              FROM core.[User] u
              JOIN meta.TenantUser tu ON tu.UserId = u.Id
              WHERE u.PublicId IN @userPublicIds
                AND tu.TenantId = @tenantId
                AND tu.IsDeleted = 0
                AND tu.IsActive = 1",
            new { userPublicIds, tenantId })).ToList();

        if (!userIds.Any()) return 0;

        // Batch-fetch user details in one query — avoids N+1 control DB connections
        var users = (await ctrlConn.QueryAsync(
            "SELECT Id, PublicId, Name, Email FROM core.[User] WHERE Id IN @userIds AND IsDeleted = 0",
            new { userIds })).ToDictionary(u => (long)u.Id);

        var added = 0;
        var sharedApps = (await conn.QueryAsync<dynamic>(new CommandDefinition(
            "SELECT AppId, AppRoleId FROM meta.GroupApp WHERE GroupId = @groupId AND IsDeleted = 0",
            new { groupId = groupId.Value }, cancellationToken: ct))).ToList();

        const string upsertSql = @"
            IF EXISTS (SELECT 1 FROM meta.AppUser WHERE AppId = @appId AND UserId = @userId)
            BEGIN
                UPDATE meta.AppUser
                SET Status = 'Active',
                    IsDeleted = 0,
                    AppRoleId = CASE WHEN IsFromGroup = 1 THEN @appRoleId ELSE AppRoleId END,
                    GroupId = CASE WHEN IsFromGroup = 1 THEN @groupId ELSE GroupId END,
                    UpdatedOn = SYSUTCDATETIME()
                WHERE AppId = @appId AND UserId = @userId
            END
            ELSE
            BEGIN
                INSERT INTO meta.AppUser (AppId, UserId, UserPublicId, UserName, UserEmail, AppRoleId, Status, ShowInUserPickers, AddedBy, CreatedOn, IsFromGroup, GroupId)
                VALUES (@appId, @userId, @userPublicId, @userName, @userEmail, @appRoleId, 'Active', 1, @addedBy, SYSUTCDATETIME(), 1, @groupId)
            END";

        foreach (var userId in userIds)
        {
            var res = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                InsertMemberSql, new { groupId = groupId.Value, userId, addedBy }, cancellationToken: ct));

            if (res > 0)
            {
                added += res;
                if (!users.TryGetValue(userId, out var user)) continue;

                foreach (var app in sharedApps)
                {
                    await conn.ExecuteAsync(new CommandDefinition(upsertSql, new
                    {
                        appId = (long)app.AppId,
                        userId,
                        userPublicId = (Guid)user.PublicId,
                        userName = (string)user.Name,
                        userEmail = (string)user.Email,
                        appRoleId = (long)app.AppRoleId,
                        groupId = groupId.Value,
                        addedBy
                    }, cancellationToken: ct));
                }
            }
        }
        return added;
    }


    private const string RemoveMemberSql = @"
        UPDATE gm
        SET gm.IsDeleted = 1
        FROM meta.GroupMember gm
        INNER JOIN meta.[Group] g ON g.Id = gm.GroupId
        WHERE g.PublicId = @groupPublicId
          AND gm.UserId = @userId
          AND gm.IsDeleted = 0;";

    public async Task<bool> RemoveMemberAsync(Guid groupPublicId, Guid userPublicId, CancellationToken ct = default)
    {
        // Resolve UserPublicId → UserId via control DB
        await using var ctrlConn = _controlFactory.Create();
        await ctrlConn.OpenAsync(ct);
        var userId = await ctrlConn.ExecuteScalarAsync<long?>(
            "SELECT Id FROM core.[User] WHERE PublicId = @userPublicId AND IsDeleted = 0",
            new { userPublicId });
        if (userId is null) return false;

        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            RemoveMemberSql, new { groupPublicId, userId = userId.Value }, cancellationToken: ct));
            
        if (rows > 0)
        {
            var groupId = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT Id FROM meta.[Group] WHERE PublicId = @groupPublicId AND IsDeleted = 0",
                new { groupPublicId }, cancellationToken: ct));
                
            if (groupId is not null)
            {
                var sharedAppIds = (await conn.QueryAsync<long>(new CommandDefinition(
                    "SELECT AppId FROM meta.GroupApp WHERE GroupId = @groupId AND IsDeleted = 0",
                    new { groupId = groupId.Value }, cancellationToken: ct))).ToList();
                    
                foreach (var appId in sharedAppIds)
                {
                    const string otherGroupSql = @"
                        SELECT TOP 1 ga.GroupId, ga.AppRoleId
                        FROM meta.GroupMember gm
                        JOIN meta.GroupApp ga ON ga.GroupId = gm.GroupId
                        WHERE gm.UserId = @userId
                          AND ga.AppId = @appId
                          AND gm.GroupId <> @groupId
                          AND gm.IsDeleted = 0 AND ga.IsDeleted = 0";
                          
                    var otherGroup = await conn.QueryFirstOrDefaultAsync<dynamic>(
                        new CommandDefinition(otherGroupSql, new { userId = userId.Value, appId, groupId = groupId.Value }, cancellationToken: ct));
                        
                    if (otherGroup is not null)
                    {
                        await conn.ExecuteAsync(new CommandDefinition(
                            @"UPDATE meta.AppUser
                              SET GroupId = @otherGroupId,
                                  AppRoleId = @otherAppRoleId,
                                  UpdatedOn = SYSUTCDATETIME()
                              WHERE AppId = @appId AND UserId = @userId AND GroupId = @groupId AND IsFromGroup = 1 AND IsDeleted = 0",
                            new { appId, userId = userId.Value, groupId = groupId.Value, otherGroupId = (long)otherGroup.GroupId, otherAppRoleId = (long)otherGroup.AppRoleId },
                            cancellationToken: ct));
                    }
                    else
                    {
                        await conn.ExecuteAsync(new CommandDefinition(
                            @"UPDATE meta.AppUser
                              SET IsDeleted = 1,
                                  Status = 'InActive',
                                  UpdatedOn = SYSUTCDATETIME()
                              WHERE AppId = @appId AND UserId = @userId AND GroupId = @groupId AND IsFromGroup = 1 AND IsDeleted = 0",
                            new { appId, userId = userId.Value, groupId = groupId.Value },
                            cancellationToken: ct));
                    }
                }
            }
            return true;
        }
        return false;
    }

    private const string ListMembersSql = @"
        SELECT gm.UserId, gm.CreatedOn AS AddedOn
        FROM meta.GroupMember gm
        INNER JOIN meta.[Group] g ON g.Id = gm.GroupId
        WHERE g.PublicId = @groupPublicId AND g.IsDeleted = 0 AND gm.IsDeleted = 0
        ORDER BY gm.CreatedOn DESC
        OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;

        SELECT COUNT(1)
        FROM meta.GroupMember gm
        INNER JOIN meta.[Group] g ON g.Id = gm.GroupId
        WHERE g.PublicId = @groupPublicId AND g.IsDeleted = 0 AND gm.IsDeleted = 0;";

    public async Task<(IEnumerable<GroupMemberDto> Items, int TotalCount)> ListMembersAsync(
        Guid groupPublicId, int page, int pageSize, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var offset = (page - 1) * pageSize;
        await using var multi = await conn.QueryMultipleAsync(new CommandDefinition(
            ListMembersSql, new { groupPublicId, offset, pageSize }, cancellationToken: ct));

        var rows = (await multi.ReadAsync<(long UserId, DateTime AddedOn)>()).ToList();
        var total = await multi.ReadSingleAsync<int>();

        if (!rows.Any())
            return (Enumerable.Empty<GroupMemberDto>(), total);

        // Fetch user details from control DB
        var userIds = rows.Select(r => r.UserId).ToList();
        await using var ctrlConn = _controlFactory.Create();
        await ctrlConn.OpenAsync(ct);
        var users = (await ctrlConn.QueryAsync(
            "SELECT Id, PublicId, Name, Email FROM core.[User] WHERE Id IN @userIds AND IsDeleted = 0",
            new { userIds })).ToDictionary(u => (long)u.Id);

        var items = rows.Select(r =>
        {
            users.TryGetValue(r.UserId, out var u);
            return new GroupMemberDto
            {
                UserPublicId = u is not null ? (Guid)u.PublicId : Guid.Empty,
                UserName     = u is not null ? (string)u.Name  : string.Empty,
                UserEmail    = u is not null ? (string)u.Email  : string.Empty,
                AddedOn      = r.AddedOn,
            };
        }).ToList();

        return (items, total);
    }

    public async Task<bool> ShareWithAppsAsync(Guid groupPublicId, IEnumerable<Guid> appPublicIds, long createdBy, Guid? appRolePublicId = null, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var groupId = await conn.ExecuteScalarAsync<long?>(
            "SELECT Id FROM meta.[Group] WHERE PublicId = @groupPublicId AND IsDeleted = 0", new { groupPublicId });
        if (groupId is null) return false;

        var apps = (await conn.QueryAsync<dynamic>(
            @"SELECT Id, DefaultAppRoleId 
               FROM meta.App 
               WHERE PublicId IN @appPublicIds AND IsDeleted = 0", 
            new { appPublicIds })).ToList();

        if (!apps.Any()) return false;

        long? customAppRoleId = null;
        if (appRolePublicId.HasValue)
        {
            customAppRoleId = await conn.ExecuteScalarAsync<long?>(
                "SELECT Id FROM meta.AppRole WHERE PublicId = @appRolePublicId AND IsDeleted = 0",
                new { appRolePublicId = appRolePublicId.Value });
        }

        const string sql = @"
            IF EXISTS (SELECT 1 FROM meta.GroupApp WHERE GroupId = @GroupId AND AppId = @AppId)
            BEGIN
                UPDATE meta.GroupApp
                SET AppRoleId = @AppRoleId, IsDeleted = 0, CreatedOn = SYSUTCDATETIME(), CreatedBy = @CreatedBy
                WHERE GroupId = @GroupId AND AppId = @AppId;
            END
            ELSE
            BEGIN
                INSERT INTO meta.GroupApp (GroupId, AppId, AppRoleId, CreatedOn, CreatedBy, IsDeleted)
                VALUES (@GroupId, @AppId, @AppRoleId, SYSUTCDATETIME(), @CreatedBy, 0);
            END";

        foreach (var app in apps)
        {
            long? appRoleId = customAppRoleId ?? app.DefaultAppRoleId;
            if (appRoleId is null)
            {
                appRoleId = await conn.ExecuteScalarAsync<long?>(
                    "SELECT TOP 1 Id FROM meta.AppRole WHERE AppId = @AppId AND IsDeleted = 0 ORDER BY IsSystem DESC, Id", 
                    new { AppId = (long)app.Id });
            }

            if (appRoleId is not null)
            {
                await conn.ExecuteAsync(new CommandDefinition(sql, new { GroupId = groupId.Value, AppId = (long)app.Id, AppRoleId = appRoleId.Value, CreatedBy = createdBy }, cancellationToken: ct));
            }
        }
        
        var appIds = apps.Select(a => (long)a.Id).ToList();
        await SyncAppUsersForGroupAndAppsAsync(groupId.Value, appIds, createdBy, ct);
        
        return true;
    }

    public async Task<bool> UnshareFromAppAsync(Guid groupPublicId, Guid appPublicId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var ids = await conn.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
            @"SELECT g.Id AS GroupId, a.Id AS AppId
              FROM meta.[Group] g
              CROSS JOIN meta.App a
              WHERE g.PublicId = @groupPublicId AND a.PublicId = @appPublicId AND g.IsDeleted = 0 AND a.IsDeleted = 0",
            new { groupPublicId, appPublicId }, cancellationToken: ct));
            
        if (ids is null) return false;
        
        const string sql = @"
            UPDATE ga
            SET ga.IsDeleted = 1
            FROM meta.GroupApp ga
            WHERE ga.GroupId = @groupId AND ga.AppId = @appId AND ga.IsDeleted = 0;";

        var rows = await conn.ExecuteAsync(new CommandDefinition(sql, new { groupId = (long)ids.GroupId, appId = (long)ids.AppId }, cancellationToken: ct));
        if (rows > 0)
        {
            await SyncAppUsersAfterUnshareAsync((long)ids.GroupId, (long)ids.AppId, ct);
            return true;
        }
        return false;
    }

    public async Task<IEnumerable<SharedAppDto>> GetSharedAppsAsync(Guid groupPublicId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        const string sql = @"
            SELECT a.PublicId AS AppPublicId, ar.PublicId AS AppRolePublicId, ar.Name AS AppRoleName
            FROM meta.GroupApp ga
            INNER JOIN meta.[Group] g ON g.Id = ga.GroupId
            INNER JOIN meta.App a ON a.Id = ga.AppId
            LEFT JOIN meta.AppRole ar ON ar.Id = ga.AppRoleId
            WHERE g.PublicId = @groupPublicId AND ga.IsDeleted = 0 AND a.IsDeleted = 0;";

        return await conn.QueryAsync<SharedAppDto>(new CommandDefinition(sql, new { groupPublicId }, cancellationToken: ct));
    }
    private async Task SyncAppUsersForGroupAndAppsAsync(long groupId, IEnumerable<long> appIds, long createdBy, CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct);
        
        var memberUserIds = (await conn.QueryAsync<long>(new CommandDefinition(
            "SELECT UserId FROM meta.GroupMember WHERE GroupId = @groupId AND IsDeleted = 0",
            new { groupId }, cancellationToken: ct))).ToList();
            
        if (!memberUserIds.Any()) return;
        
        await using var ctrlConn = _controlFactory.Create();
        await ctrlConn.OpenAsync(ct);
        var users = (await ctrlConn.QueryAsync(new CommandDefinition(
            "SELECT Id, PublicId, Name, Email FROM core.[User] WHERE Id IN @memberUserIds AND IsDeleted = 0",
            new { memberUserIds }, cancellationToken: ct))).ToDictionary(u => (long)u.Id);
            
        const string upsertSql = @"
            IF EXISTS (SELECT 1 FROM meta.AppUser WHERE AppId = @appId AND UserId = @userId)
            BEGIN
                UPDATE meta.AppUser
                SET Status = 'Active',
                    IsDeleted = 0,
                    AppRoleId = CASE WHEN IsFromGroup = 1 THEN @appRoleId ELSE AppRoleId END,
                    GroupId = CASE WHEN IsFromGroup = 1 THEN @groupId ELSE GroupId END,
                    UpdatedOn = SYSUTCDATETIME()
                WHERE AppId = @appId AND UserId = @userId
            END
            ELSE
            BEGIN
                INSERT INTO meta.AppUser (AppId, UserId, UserPublicId, UserName, UserEmail, AppRoleId, Status, ShowInUserPickers, AddedBy, CreatedOn, IsFromGroup, GroupId)
                VALUES (@appId, @userId, @userPublicId, @userName, @userEmail, @appRoleId, 'Active', 1, @addedBy, SYSUTCDATETIME(), 1, @groupId)
            END";
            
        foreach (var appId in appIds)
        {
            var appRoleId = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT AppRoleId FROM meta.GroupApp WHERE GroupId = @groupId AND AppId = @appId AND IsDeleted = 0",
                new { groupId, appId }, cancellationToken: ct));
                
            if (appRoleId is null) continue;
            
            foreach (var userId in memberUserIds)
            {
                if (!users.TryGetValue(userId, out var user)) continue;
                
                await conn.ExecuteAsync(new CommandDefinition(upsertSql, new
                {
                    appId,
                    userId,
                    userPublicId = (Guid)user.PublicId,
                    userName = (string)user.Name,
                    userEmail = (string)user.Email,
                    appRoleId = appRoleId.Value,
                    groupId,
                    addedBy = createdBy
                }, cancellationToken: ct));
            }
        }
    }

    private async Task SyncAppUsersAfterUnshareAsync(long groupId, long appId, CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct);
        
        var groupUsers = (await conn.QueryAsync<long>(new CommandDefinition(
            "SELECT UserId FROM meta.AppUser WHERE AppId = @appId AND GroupId = @groupId AND IsFromGroup = 1 AND IsDeleted = 0",
            new { appId, groupId }, cancellationToken: ct))).ToList();
            
        foreach (var userId in groupUsers)
        {
            const string otherGroupSql = @"
                SELECT TOP 1 ga.GroupId, ga.AppRoleId
                FROM meta.GroupMember gm
                JOIN meta.GroupApp ga ON ga.GroupId = gm.GroupId
                WHERE gm.UserId = @userId
                  AND ga.AppId = @appId
                  AND gm.GroupId <> @groupId
                  AND gm.IsDeleted = 0 AND ga.IsDeleted = 0";
                  
            var otherGroup = await conn.QueryFirstOrDefaultAsync<dynamic>(
                new CommandDefinition(otherGroupSql, new { userId, appId, groupId }, cancellationToken: ct));
                
            if (otherGroup is not null)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    @"UPDATE meta.AppUser
                      SET GroupId = @otherGroupId,
                          AppRoleId = @otherAppRoleId,
                          UpdatedOn = SYSUTCDATETIME()
                      WHERE AppId = @appId AND UserId = @userId",
                    new { appId, userId, otherGroupId = (long)otherGroup.GroupId, otherAppRoleId = (long)otherGroup.AppRoleId },
                    cancellationToken: ct));
            }
            else
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    @"UPDATE meta.AppUser
                      SET IsDeleted = 1,
                          Status = 'InActive',
                          UpdatedOn = SYSUTCDATETIME()
                      WHERE AppId = @appId AND UserId = @userId",
                    new { appId, userId },
                    cancellationToken: ct));
            }
        }
    }
}
