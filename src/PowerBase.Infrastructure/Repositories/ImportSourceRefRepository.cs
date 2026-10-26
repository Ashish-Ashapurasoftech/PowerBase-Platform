using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class ImportSourceRefRepository : TenantRepositoryBase, IImportSourceRefRepository
{
    private const string ListSql = """
        SELECT Id, AppId, ElementType, ElementId, SourceLogicalRef, CreatedOn, ModifiedOn
        FROM meta.ImportSourceRef
        WHERE AppId = @appId
        """;

    private const string UpsertSql = """
        UPDATE meta.ImportSourceRef
        SET ElementId = @elementId, ModifiedOn = SYSUTCDATETIME()
        WHERE AppId = @appId AND ElementType = @elementType AND SourceLogicalRef = @sourceLogicalRef;

        IF @@ROWCOUNT = 0
            INSERT INTO meta.ImportSourceRef (AppId, ElementType, ElementId, SourceLogicalRef)
            VALUES (@appId, @elementType, @elementId, @sourceLogicalRef);
        """;

    public ImportSourceRefRepository(ITenantConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    public async Task<IReadOnlyList<ImportSourceRef>> ListByAppAsync(long appId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var rows = await connection.QueryAsync<ImportSourceRef>(
            new CommandDefinition(ListSql, new { appId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task UpsertManyAsync(long appId, IReadOnlyList<(string ElementType, string SourceLogicalRef, long ElementId)> refs, CancellationToken ct = default)
    {
        if (refs.Count == 0)
            return;

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        foreach (var r in refs)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                UpsertSql,
                new { appId, elementType = r.ElementType, sourceLogicalRef = r.SourceLogicalRef, elementId = r.ElementId },
                transaction, cancellationToken: ct));
        }
        await transaction.CommitAsync(ct);
    }

    public async Task UpsertAsync(long appId, string elementType, string sourceLogicalRef, long elementId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition(UpsertSql, new { appId, elementType, sourceLogicalRef, elementId }, cancellationToken: ct));
    }
}
