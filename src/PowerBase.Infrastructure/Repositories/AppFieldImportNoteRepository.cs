using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class AppFieldImportNoteRepository : TenantRepositoryBase, IAppFieldImportNoteRepository
{
    private const string InsertSql = """
        INSERT INTO meta.AppFieldImportNote
            (PublicId, AppId, AppTableId, AppFieldId, Severity, Code, Message, RecommendedAction, SourceLogicalRef, SourceType,
             SourceFieldId, IsPlaceholder, OriginalDefinition, OriginalMetadata)
        VALUES
            (NEWID(), @AppId, @AppTableId, @AppFieldId, @Severity, @Code, @Message, @RecommendedAction, @SourceLogicalRef, @SourceType,
             @SourceFieldId, @IsPlaceholder, @OriginalDefinition, @OriginalMetadata)
        """;

    private const string ListByFieldSql = """
        SELECT Id, PublicId, AppId, AppTableId, AppFieldId, Severity, Code, Message, RecommendedAction, SourceLogicalRef, SourceType,
               SourceFieldId, IsPlaceholder, OriginalDefinition, OriginalMetadata, CreatedOn, ResolvedOn, ResolvedBy
        FROM meta.AppFieldImportNote
        WHERE AppFieldId = @appFieldId
        ORDER BY CASE WHEN ResolvedOn IS NULL THEN 0 ELSE 1 END, Id
        """;

    private const string SummarizeByTableSql = """
        SELECT AppFieldId,
               COUNT(*) AS ActiveCount,
               SUM(CASE WHEN Severity = 'Warning' THEN 1 ELSE 0 END) AS WarningCount,
               SUM(CASE WHEN Severity = 'Error' THEN 1 ELSE 0 END) AS ErrorCount,
               CAST(MAX(CASE WHEN IsPlaceholder = 1 THEN 1 ELSE 0 END) AS BIT) AS HasPlaceholder
        FROM meta.AppFieldImportNote n
        WHERE n.AppTableId = @appTableId AND n.ResolvedOn IS NULL
          -- A field somebody has changed since the import (a later Update or Restore in its audit history) is no longer flagged.
          AND NOT EXISTS (SELECT 1 FROM meta.AppFieldVersion v
                          WHERE v.AppFieldId = n.AppFieldId AND v.ChangeType <> 3 AND v.ChangedOn > n.CreatedOn)
        GROUP BY n.AppFieldId
        """;

    private const string ResolveByFieldSql = """
        UPDATE meta.AppFieldImportNote
        SET ResolvedOn = SYSUTCDATETIME(), ResolvedBy = @userId
        WHERE AppFieldId = @appFieldId AND ResolvedOn IS NULL
        """;

    private const string ResolveOneSql = """
        UPDATE meta.AppFieldImportNote
        SET ResolvedOn = SYSUTCDATETIME(), ResolvedBy = @userId
        WHERE AppFieldId = @appFieldId AND PublicId = @notePublicId AND ResolvedOn IS NULL
        """;

    public AppFieldImportNoteRepository(ITenantConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    public async Task AddManyAsync(IReadOnlyList<AppFieldImportNote> notes, CancellationToken ct = default)
    {
        if (notes.Count == 0)
            return;

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        foreach (var note in notes)
            await connection.ExecuteAsync(new CommandDefinition(InsertSql, note, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<AppFieldImportNote>> ListByFieldAsync(long appFieldId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var rows = await connection.QueryAsync<AppFieldImportNote>(
            new CommandDefinition(ListByFieldSql, new { appFieldId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<AppFieldImportNoteSummary>> SummarizeByTableAsync(long appTableId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var rows = await connection.QueryAsync<AppFieldImportNoteSummary>(
            new CommandDefinition(SummarizeByTableSql, new { appTableId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<int> ResolveByFieldAsync(long appFieldId, long userId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(ResolveByFieldSql, new { appFieldId, userId }, cancellationToken: ct));
    }

    public async Task<bool> ResolveAsync(long appFieldId, Guid notePublicId, long userId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(ResolveOneSql, new { appFieldId, notePublicId, userId }, cancellationToken: ct)) > 0;
    }
}
