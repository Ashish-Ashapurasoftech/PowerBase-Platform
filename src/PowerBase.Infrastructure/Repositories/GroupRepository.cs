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
        INSERT INTO meta.[Group] (PublicId, Name, Description, AppRoleId, CreatedOn, CreatedBy)
        OUTPUT INSERTED.Id, INSERTED.PublicId, INSERTED.Name, INSERTED.Description, INSERTED.AppRoleId,
               INSERTED.CreatedOn, INSERTED.CreatedBy, INSERTED.IsDeleted, INSERTED.RowVersion
        VALUES (@PublicId, @Name, @Description, @AppRoleId, @CreatedOn, @CreatedBy);";

    public async Task<Group> CreateAsync(Group group, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<Group>(new CommandDefinition(InsertGroupSql, group, cancellationToken: ct));
    }

    // ── READ ─────────────────────────────────────────────────────────────────

    private const string GetByPublicIdSql = @"
        SELECT g.Id, g.PublicId, g.Name, g.Description, g.AppRoleId, g.CreatedOn,
               ar.PublicId AS AppRolePublicId, ar.Name AS AppRoleName,
               (SELECT COUNT(1) FROM meta.GroupMember gm WHERE gm.GroupId = g.Id AND gm.IsDeleted = 0) AS MemberCount
        FROM meta.[Group] g
        LEFT JOIN meta.AppRole ar ON ar.Id = g.AppRoleId
        WHERE g.PublicId = @publicId AND g.IsDeleted = 0;";

    public async Task<GroupDto?> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<GroupDto>(new CommandDefinition(
            GetByPublicIdSql, new { publicId }, cancellationToken: ct));
    }

    private const string ListPagedSql = @"
        SELECT g.Id, g.PublicId, g.Name, g.Description, g.AppRoleId, g.CreatedOn,
               ar.PublicId AS AppRolePublicId, ar.Name AS AppRoleName,
               (SELECT COUNT(1) FROM meta.GroupMember gm WHERE gm.GroupId = g.Id AND gm.IsDeleted = 0) AS MemberCount
        FROM meta.[Group] g
        LEFT JOIN meta.AppRole ar ON ar.Id = g.AppRoleId
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
        SET Name = @name, Description = @description, AppRoleId = @appRoleId, ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy
        WHERE PublicId = @publicId AND IsDeleted = 0;
        SELECT @@ROWCOUNT;";

    public async Task<bool> UpdateAsync(Guid publicId, string name, string? description, long? appRoleId, long modifiedBy, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            UpdateGroupSql, new { publicId, name, description, appRoleId, modifiedBy }, cancellationToken: ct));
        return rows > 0;
    }

    // ── DELETE ────────────────────────────────────────────────────────────────

    private const string DeleteGroupSql = @"
        UPDATE meta.GroupMember SET IsDeleted = 1 WHERE GroupId = (SELECT Id FROM meta.[Group] WHERE PublicId = @publicId AND IsDeleted = 0);
        UPDATE meta.[Group]
        SET IsDeleted = 1, DeletedOn = SYSUTCDATETIME(), DeletedBy = @deletedBy
        WHERE PublicId = @publicId AND IsDeleted = 0;
        SELECT @@ROWCOUNT;";

    public async Task<bool> DeleteAsync(Guid publicId, long deletedBy, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
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
            ExistsByNameSql, new { name, excludePublicId = (object?)excludePublicId ?? DBNull.Value }, cancellationToken: ct));
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

        var added = 0;
        foreach (var userId in userIds)
        {
            var res = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                InsertMemberSql, new { groupId = groupId.Value, userId, addedBy }, cancellationToken: ct));
            added += res;
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
        return rows > 0;
    }

    private const string ListMembersSql = @"
        SELECT gm.UserId, gm.CreatedOn AS AddedOn
        FROM meta.GroupMember gm
        INNER JOIN meta.[Group] g ON g.Id = gm.GroupId
        WHERE g.PublicId = @groupPublicId AND g.IsDeleted = 0 AND gm.IsDeleted = 0
        ORDER BY gm.UserId
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
        }).OrderBy(m => m.UserName).ToList();

        return (items, total);
    }

    public async Task<bool> ShareWithAppsAsync(Guid groupPublicId, IEnumerable<Guid> appPublicIds, long createdBy, CancellationToken ct = default)
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
            long? appRoleId = app.DefaultAppRoleId;
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
        return true;
    }

    public async Task<bool> UnshareFromAppAsync(Guid groupPublicId, Guid appPublicId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        const string sql = @"
            UPDATE ga
            SET ga.IsDeleted = 1
            FROM meta.GroupApp ga
            INNER JOIN meta.[Group] g ON g.Id = ga.GroupId
            INNER JOIN meta.App a ON a.Id = ga.AppId
            WHERE g.PublicId = @groupPublicId AND a.PublicId = @appPublicId AND ga.IsDeleted = 0;";

        var rows = await conn.ExecuteAsync(new CommandDefinition(sql, new { groupPublicId, appPublicId }, cancellationToken: ct));
        return rows > 0;
    }
}
