using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.FieldReferences;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

/// <summary>meta.FieldReference — see migration 064 for what the table holds and why it has no foreign keys.</summary>
public class FieldReferenceRepository : TenantRepositoryBase, IFieldReferenceRepository
{
    private const string DeleteForSourceSql = """
        DELETE FROM meta.FieldReference WHERE SourceType = @sourceType AND SourceId = @sourceId
        """;

    private const string InsertSql = """
        INSERT INTO meta.FieldReference (SourceType, SourceId, SourceTableId, TargetFieldId, Usage)
        VALUES (@SourceType, @SourceId, @SourceTableId, @TargetFieldId, @Usage)
        """;

    // The one place a source's public id turns into its internal id: a switch over fixed table
    // names (never user input), so the interpolation below can't be an injection point.
    private static string SourceTable(string sourceType) => sourceType switch
    {
        FieldReferenceSourceTypes.Report => "meta.Report",
        FieldReferenceSourceTypes.Form => "meta.Form",
        FieldReferenceSourceTypes.FormRule => "meta.FormRule",
        FieldReferenceSourceTypes.Field => "meta.AppField",
        _ => throw new ArgumentOutOfRangeException(nameof(sourceType), sourceType, "Unknown field-reference source type."),
    };

    private const string DeleteRuleRowsOfFormSql = """
        DELETE fr
        FROM   meta.FieldReference fr
        JOIN   meta.FormRule r ON r.Id = fr.SourceId
        JOIN   meta.Form     f ON f.Id = r.FormId
        WHERE  fr.SourceType = 'FormRule'
          AND  f.PublicId    = @formPublicId
        """;

    // Each source type joins its own table for the display name and drops sources that were
    // soft-deleted, so the Usage tab never lists something the user can no longer open.
    private const string ListByTargetSql = """
        SELECT fr.SourceType, fr.SourceId, s.PublicId AS SourcePublicId, s.Name AS SourceName,
               fr.SourceTableId, t.PublicId AS SourceTablePublicId, t.Name AS SourceTableName,
               CAST(NULL AS UNIQUEIDENTIFIER) AS ParentPublicId, CAST(NULL AS NVARCHAR(200)) AS ParentName, fr.Usage
        FROM   meta.FieldReference fr
        JOIN   meta.Report   s ON s.Id = fr.SourceId AND s.IsDeleted = 0
        JOIN   meta.AppTable t ON t.Id = fr.SourceTableId
        WHERE  fr.TargetFieldId = @targetFieldId AND fr.SourceType = 'Report'

        UNION ALL

        SELECT fr.SourceType, fr.SourceId, s.PublicId, s.Name,
               fr.SourceTableId, t.PublicId, t.Name,
               CAST(NULL AS UNIQUEIDENTIFIER), CAST(NULL AS NVARCHAR(200)), fr.Usage
        FROM   meta.FieldReference fr
        JOIN   meta.Form     s ON s.Id = fr.SourceId AND s.IsDeleted = 0
        JOIN   meta.AppTable t ON t.Id = fr.SourceTableId
        WHERE  fr.TargetFieldId = @targetFieldId AND fr.SourceType = 'Form'

        UNION ALL

        SELECT fr.SourceType, fr.SourceId, s.PublicId, s.Name,
               fr.SourceTableId, t.PublicId, t.Name,
               f.PublicId, f.Name, fr.Usage
        FROM   meta.FieldReference fr
        JOIN   meta.FormRule s ON s.Id = fr.SourceId AND s.IsDeleted = 0
        JOIN   meta.Form     f ON f.Id = s.FormId AND f.IsDeleted = 0
        JOIN   meta.AppTable t ON t.Id = fr.SourceTableId
        WHERE  fr.TargetFieldId = @targetFieldId AND fr.SourceType = 'FormRule'

        UNION ALL

        SELECT fr.SourceType, fr.SourceId, s.PublicId, COALESCE(NULLIF(s.Label, ''), s.Name),
               fr.SourceTableId, t.PublicId, t.Name,
               CAST(NULL AS UNIQUEIDENTIFIER), CAST(NULL AS NVARCHAR(200)), fr.Usage
        FROM   meta.FieldReference fr
        JOIN   meta.AppField s ON s.Id = fr.SourceId AND s.IsDeleted = 0
        JOIN   meta.AppTable t ON t.Id = fr.SourceTableId
        WHERE  fr.TargetFieldId = @targetFieldId AND fr.SourceType = 'Field'
        """;

    private const string ListReportSourcesSql = """
        SELECT r.Id, r.PublicId, r.AppTableId, r.Name, r.Definition
        FROM   meta.Report r
        WHERE  r.AppTableId = @tableId
          AND  r.IsDeleted  = 0
        """;

    public FieldReferenceRepository(ITenantConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    public async Task ReplaceForSourceAsync(string sourceType, long sourceId, IReadOnlyCollection<FieldReferenceRow> rows, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await connection.ExecuteAsync(new CommandDefinition(DeleteForSourceSql, new { sourceType, sourceId }, tx, cancellationToken: ct));
        if (rows.Count > 0)
            await connection.ExecuteAsync(new CommandDefinition(InsertSql, rows, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
    }

    public async Task DeleteBySourcePublicIdAsync(string sourceType, Guid sourcePublicId, CancellationToken ct = default)
    {
        var sql = $"""
            DELETE fr
            FROM   meta.FieldReference fr
            JOIN   {SourceTable(sourceType)} s ON s.Id = fr.SourceId
            WHERE  fr.SourceType = @sourceType
              AND  s.PublicId    = @sourcePublicId
            """;
        await using var connection = await OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(sql, new { sourceType, sourcePublicId }, cancellationToken: ct));
    }

    public async Task DeleteRuleRowsOfFormAsync(Guid formPublicId, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(DeleteRuleRowsOfFormSql, new { formPublicId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<FieldReferenceItem>> ListByTargetFieldAsync(long targetFieldId, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        var items = await connection.QueryAsync<FieldReferenceItem>(
            new CommandDefinition(ListByTargetSql, new { targetFieldId }, cancellationToken: ct));
        return items.ToList();
    }

    public async Task<IReadOnlyList<Report>> ListReportSourcesAsync(long tableId, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        var reports = await connection.QueryAsync<Report>(
            new CommandDefinition(ListReportSourcesSql, new { tableId }, cancellationToken: ct));
        return reports.ToList();
    }

    public async Task<bool> IsTableIndexedAsync(long tableId, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM meta.FieldReferenceIndexState WHERE AppTableId = @tableId) THEN 1 ELSE 0 END",
            new { tableId }, cancellationToken: ct));
    }

    public async Task MarkTableIndexedAsync(long tableId, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            IF EXISTS (SELECT 1 FROM meta.FieldReferenceIndexState WHERE AppTableId = @tableId)
                UPDATE meta.FieldReferenceIndexState SET IndexedOn = SYSUTCDATETIME() WHERE AppTableId = @tableId
            ELSE
                INSERT INTO meta.FieldReferenceIndexState (AppTableId) VALUES (@tableId)
            """, new { tableId }, cancellationToken: ct));
    }

    public async Task ClearTableAsync(long tableId, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            DELETE FROM meta.FieldReference WHERE SourceTableId = @tableId;
            DELETE FROM meta.FieldReferenceIndexState WHERE AppTableId = @tableId;
            """, new { tableId }, cancellationToken: ct));
    }
}
