using System.Data;
using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.Versioning;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class FieldVersionRepository : TenantRepositoryBase, IFieldVersionRepository
{
    public FieldVersionRepository(ITenantConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    private const string GetNextVersionSql = """
        SELECT ISNULL(MAX(Version), 0) + 1
        FROM meta.AppFieldVersion WITH (UPDLOCK, HOLDLOCK)
        WHERE AppFieldId = @appFieldId
        """;

    public async Task<int> GetNextVersionNumberAsync(long appFieldId, IDbTransaction transaction, CancellationToken ct = default)
    {
        return await transaction.Connection!.ExecuteScalarAsync<int>(
            new CommandDefinition(GetNextVersionSql, new { appFieldId }, transaction, cancellationToken: ct));
    }

    public async Task<int> GetCurrentVersionNumberAsync(long appFieldId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT ISNULL(MAX(Version), 0) FROM meta.AppFieldVersion WHERE AppFieldId = @appFieldId",
                new { appFieldId }, cancellationToken: ct));
    }

    private const string InsertVersionSql = """
        INSERT INTO meta.AppFieldVersion
            (AppFieldId, Version, ChangeType, RestoredFromVersion, CommitMessage, ChangedByUserId, ChangedByName, ChangedOn, SnapshotJson)
        OUTPUT INSERTED.Id
        VALUES
            (@AppFieldId, @Version, @ChangeType, @RestoredFromVersion, @CommitMessage, @ChangedByUserId, @ChangedByName, @ChangedOn, @SnapshotJson)
        """;

    private const string InsertChangeSql = """
        INSERT INTO meta.AppFieldVersionChange (AppFieldVersionId, PropertyName, OldValue, NewValue)
        VALUES (@AppFieldVersionId, @PropertyName, @OldValue, @NewValue)
        """;

    public async Task InsertVersionAsync(AppFieldVersion version, IReadOnlyList<FieldChangeEntry> changes,
        IDbTransaction transaction, CancellationToken ct = default)
    {
        var connection = transaction.Connection!;
        var versionId = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(InsertVersionSql, version, transaction, cancellationToken: ct));

        var changeRows = changes.Select(c => new
        {
            AppFieldVersionId = versionId,
            c.PropertyName,
            c.OldValue,
            c.NewValue,
        });
        await connection.ExecuteAsync(new CommandDefinition(InsertChangeSql, changeRows, transaction, cancellationToken: ct));
    }

    private const string ListByFieldSql = """
        SELECT v.Version, v.ChangeType, v.RestoredFromVersion, v.CommitMessage, v.ChangedOn,
               v.ChangedByName,
               ISNULL(STUFF((
                   SELECT ', ' + c.PropertyName
                   FROM meta.AppFieldVersionChange c
                   WHERE c.AppFieldVersionId = v.Id
                   ORDER BY c.Id
                   FOR XML PATH('')
               ), 1, 2, ''), '') AS ChangedPropertiesSummary
        FROM meta.AppFieldVersion v
        WHERE v.AppFieldId = @appFieldId
        ORDER BY v.Version DESC
        OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY
        """;

    private const string CountByFieldSql =
        "SELECT COUNT(1) FROM meta.AppFieldVersion WHERE AppFieldId = @appFieldId";

    public async Task<(IReadOnlyList<FieldVersionListItem> Items, int Total)> ListByFieldAsync(
        long appFieldId, int page, int pageSize, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var offset = (page - 1) * pageSize;

        var items = (await connection.QueryAsync<FieldVersionListItem>(
            new CommandDefinition(ListByFieldSql, new { appFieldId, offset, pageSize }, cancellationToken: ct))).ToList();
        var total = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(CountByFieldSql, new { appFieldId }, cancellationToken: ct));

        return (items, total);
    }

    private const string GetByFieldAndVersionSql = """
        SELECT Id, PublicId, AppFieldId, Version, ChangeType, RestoredFromVersion,
               CommitMessage, ChangedByUserId, ChangedByName, ChangedOn, SnapshotJson
        FROM meta.AppFieldVersion
        WHERE AppFieldId = @appFieldId AND Version = @version
        """;

    public async Task<AppFieldVersion?> GetByFieldAndVersionAsync(long appFieldId, int version, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<AppFieldVersion>(
            new CommandDefinition(GetByFieldAndVersionSql, new { appFieldId, version }, cancellationToken: ct));
    }

    private const string ListChangesSql = """
        SELECT Id, AppFieldVersionId, PropertyName, OldValue, NewValue
        FROM meta.AppFieldVersionChange
        WHERE AppFieldVersionId = @appFieldVersionId
        ORDER BY Id
        """;

    public async Task<IReadOnlyList<AppFieldVersionChange>> ListChangesAsync(long appFieldVersionId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var rows = await connection.QueryAsync<AppFieldVersionChange>(
            new CommandDefinition(ListChangesSql, new { appFieldVersionId }, cancellationToken: ct));
        return rows.ToList();
    }
}
