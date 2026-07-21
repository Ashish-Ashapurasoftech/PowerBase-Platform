using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class RelationshipRepository : TenantRepositoryBase, IRelationshipRepository
{
    private const string SelectColumns = """
        Id, PublicId, AppId, ParentTableId, ChildTableId, ReferenceFieldId, ReferenceFid, ProxyFieldId,
        IsDeleted, CreatedOn, CreatedBy, ModifiedOn, ModifiedBy
        """;

    private const string InsertSql = """
        INSERT INTO meta.Relationship
            (AppId, ParentTableId, ChildTableId, ReferenceFieldId, ReferenceFid, ProxyFieldId, IsDeleted, CreatedOn, CreatedBy)
        OUTPUT INSERTED.Id, INSERTED.PublicId
        VALUES (@appId, @parentTableId, @childTableId, @referenceFieldId, @referenceFid, @proxyFieldId, 0, SYSUTCDATETIME(), @createdBy)
        """;

    private const string UpdateProxyFieldSql = """
        UPDATE meta.Relationship SET ProxyFieldId = @proxyFieldId, ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy
        WHERE Id = @id
        """;

    private const string UpdateReferenceFieldSql = """
        UPDATE meta.Relationship
        SET ReferenceFieldId = @referenceFieldId, ReferenceFid = @referenceFid,
            ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy
        WHERE Id = @id
        """;

    private const string GetByPublicIdSql = $"SELECT {SelectColumns} FROM meta.Relationship WHERE PublicId = @publicId AND IsDeleted = 0";
    private const string GetByIdSql = $"SELECT {SelectColumns} FROM meta.Relationship WHERE Id = @id AND IsDeleted = 0";
    private const string ListByAppSql = $"SELECT {SelectColumns} FROM meta.Relationship WHERE AppId = @appId AND IsDeleted = 0 ORDER BY Id";
    private const string ListByTableSql = $"SELECT {SelectColumns} FROM meta.Relationship WHERE (ParentTableId = @tableId OR ChildTableId = @tableId) AND IsDeleted = 0 ORDER BY Id";
    private const string ListByParentTableSql = $"SELECT {SelectColumns} FROM meta.Relationship WHERE ParentTableId = @tableId AND IsDeleted = 0 ORDER BY Id";
    private const string ListByChildTableSql = $"SELECT {SelectColumns} FROM meta.Relationship WHERE ChildTableId = @tableId AND IsDeleted = 0 ORDER BY Id";

    private const string SoftDeleteSql = """
        UPDATE meta.Relationship SET IsDeleted = 1, DeletedOn = SYSUTCDATETIME(), DeletedBy = @deletedBy
        WHERE PublicId = @publicId AND IsDeleted = 0
        """;

    public RelationshipRepository(ITenantConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    public async Task<(long Id, Guid PublicId)> CreateAsync(Relationship rel, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var row = await connection.QuerySingleAsync(
            new CommandDefinition(InsertSql, new
            {
                appId = rel.AppId,
                parentTableId = rel.ParentTableId,
                childTableId = rel.ChildTableId,
                referenceFieldId = rel.ReferenceFieldId,
                referenceFid = rel.ReferenceFid,
                proxyFieldId = rel.ProxyFieldId,
                createdBy = QueryContext.UserId,
            }, cancellationToken: ct));
        return ((long)row.Id, (Guid)row.PublicId);
    }

    public async Task UpdateProxyFieldAsync(long id, long? proxyFieldId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition(UpdateProxyFieldSql, new { id, proxyFieldId, modifiedBy = QueryContext.UserId }, cancellationToken: ct));
    }

    public async Task UpdateReferenceFieldAsync(long id, long referenceFieldId, int referenceFid, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition(UpdateReferenceFieldSql, new { id, referenceFieldId, referenceFid, modifiedBy = QueryContext.UserId }, cancellationToken: ct));
    }

    public async Task<Relationship?> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<Relationship>(
            new CommandDefinition(GetByPublicIdSql, new { publicId }, cancellationToken: ct));
    }

    public async Task<Relationship?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<Relationship>(
            new CommandDefinition(GetByIdSql, new { id }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<Relationship>> ListByAppAsync(long appId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var rows = await connection.QueryAsync<Relationship>(
            new CommandDefinition(ListByAppSql, new { appId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<Relationship>> ListByTableAsync(long tableId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var rows = await connection.QueryAsync<Relationship>(
            new CommandDefinition(ListByTableSql, new { tableId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<Relationship>> ListByParentTableAsync(long parentTableId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var rows = await connection.QueryAsync<Relationship>(
            new CommandDefinition(ListByParentTableSql, new { tableId = parentTableId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<Relationship>> ListByChildTableAsync(long childTableId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var rows = await connection.QueryAsync<Relationship>(
            new CommandDefinition(ListByChildTableSql, new { tableId = childTableId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<int> SoftDeleteAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteAsync(
            new CommandDefinition(SoftDeleteSql, new { publicId, deletedBy = QueryContext.UserId }, cancellationToken: ct));
    }
}
