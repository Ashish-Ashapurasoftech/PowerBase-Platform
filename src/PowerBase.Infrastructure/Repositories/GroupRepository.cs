using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Groups.Common;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class GroupRepository : TenantRepositoryBase, IGroupRepository
{
    public GroupRepository(
        ITenantConnectionFactory connectionFactory, 
        IQueryContext queryContext)
        : base(connectionFactory, queryContext)
    {
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
        SELECT PublicId, Name, Description, CreatedOn
        FROM meta.[Group]
        WHERE PublicId = @publicId AND IsDeleted = 0;";

    public async Task<GroupDto?> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<GroupDto>(new CommandDefinition(
            GetByPublicIdSql, new { publicId }, cancellationToken: ct));
    }

    private const string ListPagedSql = @"
        SELECT g.PublicId, g.Name, g.Description, g.CreatedOn
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
}
