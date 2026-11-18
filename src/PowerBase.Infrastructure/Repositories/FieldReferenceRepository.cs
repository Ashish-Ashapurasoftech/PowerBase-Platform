using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.FieldReferences;
using PowerBase.Application.Pipelines;
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
        FieldReferenceSourceTypes.Relationship => "meta.Relationship",
        FieldReferenceSourceTypes.Dashboard => "meta.Page",
        FieldReferenceSourceTypes.CustomRule => "meta.AppRole", // a role's rules are filed under the role
        FieldReferenceSourceTypes.PowerFlow => "meta.Pipeline",
        FieldReferenceSourceTypes.Import => "meta.ImportDefinition",
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
               CAST(NULL AS UNIQUEIDENTIFIER) AS ParentPublicId, CAST(NULL AS NVARCHAR(200)) AS ParentName, fr.Usage,
               CAST(s.OwnerId AS BIGINT) AS OwnerId, CAST(s.Visibility AS NVARCHAR(30)) AS Visibility
        FROM   meta.FieldReference fr
        JOIN   meta.Report   s ON s.Id = fr.SourceId AND s.IsDeleted = 0
        JOIN   meta.AppTable t ON t.Id = fr.SourceTableId
        WHERE  fr.TargetFieldId = @targetFieldId AND fr.SourceType = 'Report'

        UNION ALL

        SELECT fr.SourceType, fr.SourceId, s.PublicId, s.Name,
               fr.SourceTableId, t.PublicId, t.Name,
               CAST(NULL AS UNIQUEIDENTIFIER), CAST(NULL AS NVARCHAR(200)), fr.Usage,
               CAST(NULL AS BIGINT), CAST(NULL AS NVARCHAR(30))
        FROM   meta.FieldReference fr
        JOIN   meta.Form     s ON s.Id = fr.SourceId AND s.IsDeleted = 0
        JOIN   meta.AppTable t ON t.Id = fr.SourceTableId
        WHERE  fr.TargetFieldId = @targetFieldId AND fr.SourceType = 'Form'

        UNION ALL

        SELECT fr.SourceType, fr.SourceId, s.PublicId, s.Name,
               fr.SourceTableId, t.PublicId, t.Name,
               f.PublicId, f.Name, fr.Usage,
               CAST(NULL AS BIGINT), CAST(NULL AS NVARCHAR(30))
        FROM   meta.FieldReference fr
        JOIN   meta.FormRule s ON s.Id = fr.SourceId AND s.IsDeleted = 0
        JOIN   meta.Form     f ON f.Id = s.FormId AND f.IsDeleted = 0
        JOIN   meta.AppTable t ON t.Id = fr.SourceTableId
        WHERE  fr.TargetFieldId = @targetFieldId AND fr.SourceType = 'FormRule'

        UNION ALL

        SELECT fr.SourceType, fr.SourceId, s.PublicId, COALESCE(NULLIF(s.Label, ''), s.Name),
               fr.SourceTableId, t.PublicId, t.Name,
               CAST(NULL AS UNIQUEIDENTIFIER), CAST(NULL AS NVARCHAR(200)), fr.Usage,
               CAST(NULL AS BIGINT), CAST(NULL AS NVARCHAR(30))
        FROM   meta.FieldReference fr
        JOIN   meta.AppField s ON s.Id = fr.SourceId AND s.IsDeleted = 0
        JOIN   meta.AppTable t ON t.Id = fr.SourceTableId
        WHERE  fr.TargetFieldId = @targetFieldId AND fr.SourceType = 'Field'

        UNION ALL

        -- A relationship has no name of its own: it reads "Parent ◂ Child" and is filed under its child table.
        SELECT fr.SourceType, fr.SourceId, s.PublicId, CONCAT(pt.Name, N' ', NCHAR(9666), N' ', ct.Name),
               fr.SourceTableId, t.PublicId, t.Name,
               CAST(NULL AS UNIQUEIDENTIFIER), CAST(NULL AS NVARCHAR(200)), fr.Usage,
               CAST(NULL AS BIGINT), CAST(NULL AS NVARCHAR(30))
        FROM   meta.FieldReference fr
        JOIN   meta.Relationship s  ON s.Id  = fr.SourceId AND s.IsDeleted = 0
        JOIN   meta.AppTable     pt ON pt.Id = s.ParentTableId
        JOIN   meta.AppTable     ct ON ct.Id = s.ChildTableId
        JOIN   meta.AppTable     t  ON t.Id  = fr.SourceTableId
        WHERE  fr.TargetFieldId = @targetFieldId AND fr.SourceType = 'Relationship'

        UNION ALL

        SELECT fr.SourceType, fr.SourceId, s.PublicId, s.Name,
               fr.SourceTableId, t.PublicId, t.Name,
               CAST(NULL AS UNIQUEIDENTIFIER), CAST(NULL AS NVARCHAR(200)), fr.Usage,
               CAST(s.OwnerId AS BIGINT), CAST(s.Visibility AS NVARCHAR(30))
        FROM   meta.FieldReference fr
        JOIN   meta.Page     s ON s.Id = fr.SourceId AND s.IsDeleted = 0 AND s.PageType = 'Dashboard'
        JOIN   meta.AppTable t ON t.Id = fr.SourceTableId
        WHERE  fr.TargetFieldId = @targetFieldId AND fr.SourceType = 'Dashboard'

        UNION ALL

        -- The source is the role; the row's table is the table the rule is written for.
        SELECT fr.SourceType, fr.SourceId, s.PublicId, s.Name,
               fr.SourceTableId, t.PublicId, t.Name,
               CAST(NULL AS UNIQUEIDENTIFIER), CAST(NULL AS NVARCHAR(200)), fr.Usage,
               CAST(NULL AS BIGINT), CAST(NULL AS NVARCHAR(30))
        FROM   meta.FieldReference fr
        JOIN   meta.AppRole  s ON s.Id = fr.SourceId AND s.IsDeleted = 0
        JOIN   meta.AppTable t ON t.Id = fr.SourceTableId
        WHERE  fr.TargetFieldId = @targetFieldId AND fr.SourceType = 'CustomRule'

        UNION ALL

        -- A PowerFlow: the row's table is the table of the field it uses. Usage is "step:<RefId>"; it is shown as the step's label.
        SELECT fr.SourceType, fr.SourceId, s.PublicId, s.Name,
               fr.SourceTableId, t.PublicId, t.Name,
               CAST(NULL AS UNIQUEIDENTIFIER), CAST(NULL AS NVARCHAR(200)),
               CAST(COALESCE(NULLIF(ps.Label, N''), fr.Usage) AS NVARCHAR(200)),
               CAST(s.CreatedBy AS BIGINT), CAST(NULL AS NVARCHAR(30))
        FROM   meta.FieldReference fr
        JOIN   meta.Pipeline s ON s.Id = fr.SourceId AND s.IsDeleted = 0
        JOIN   meta.AppTable t ON t.Id = fr.SourceTableId
        LEFT JOIN meta.PipelineStep ps ON ps.PipelineId = s.Id AND ps.IsDeleted = 0 AND ps.RefId = SUBSTRING(fr.Usage, 6, 40)
        WHERE  fr.TargetFieldId = @targetFieldId AND fr.SourceType = 'PowerFlow'

        UNION ALL

        -- An import: filed under the table of the field it names; the owner is whoever created it.
        SELECT fr.SourceType, fr.SourceId, s.PublicId, s.Name,
               fr.SourceTableId, t.PublicId, t.Name,
               CAST(NULL AS UNIQUEIDENTIFIER), CAST(NULL AS NVARCHAR(200)), fr.Usage,
               CAST(s.CreatedBy AS BIGINT), CAST(NULL AS NVARCHAR(30))
        FROM   meta.FieldReference fr
        JOIN   meta.ImportDefinition s ON s.Id = fr.SourceId AND s.IsDeleted = 0
        JOIN   meta.AppTable t ON t.Id = fr.SourceTableId
        WHERE  fr.TargetFieldId = @targetFieldId AND fr.SourceType = 'Import'
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
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM meta.FieldReferenceIndexState WHERE AppTableId = @tableId AND IndexVersion >= @version) THEN 1 ELSE 0 END",
            new { tableId, version = FieldReferenceIndexVersion.Current }, cancellationToken: ct));
    }

    public async Task<IReadOnlySet<long>> ListIndexedTableIdsAsync(IReadOnlyCollection<long> tableIds, CancellationToken ct = default)
    {
        if (tableIds.Count == 0) return new HashSet<long>();
        await using var connection = await OpenConnectionAsync(ct);
        var ids = await connection.QueryAsync<long>(new CommandDefinition(
            "SELECT AppTableId FROM meta.FieldReferenceIndexState WHERE AppTableId IN @tableIds AND IndexVersion >= @version",
            new { tableIds, version = FieldReferenceIndexVersion.Current }, cancellationToken: ct));
        return ids.ToHashSet();
    }

    public async Task MarkTableIndexedAsync(long tableId, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            IF EXISTS (SELECT 1 FROM meta.FieldReferenceIndexState WHERE AppTableId = @tableId)
                UPDATE meta.FieldReferenceIndexState SET IndexedOn = SYSUTCDATETIME(), IndexVersion = @version WHERE AppTableId = @tableId
            ELSE
                INSERT INTO meta.FieldReferenceIndexState (AppTableId, IndexVersion) VALUES (@tableId, @version)
            """, new { tableId, version = FieldReferenceIndexVersion.Current }, cancellationToken: ct));
    }

    public async Task InsertRowsAsync(IReadOnlyCollection<FieldReferenceRow> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        await using var connection = await OpenConnectionAsync(ct);
        // Skips a row that is already there (a save re-indexed the same source while a rebuild was running),
        // so the table's unique key can never turn a rebuild into a failure.
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO meta.FieldReference (SourceType, SourceId, SourceTableId, TargetFieldId, Usage)
            SELECT @SourceType, @SourceId, @SourceTableId, @TargetFieldId, @Usage
            WHERE NOT EXISTS (
                SELECT 1 FROM meta.FieldReference
                WHERE SourceType = @SourceType AND SourceId = @SourceId AND TargetFieldId = @TargetFieldId AND Usage = @Usage)
            """, rows, cancellationToken: ct));
    }

    // ── Source readers for relationships, dashboards and custom rules ───────────────────────────

    public async Task<DashboardSource?> GetDashboardSourceAsync(Guid pagePublicId, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<DashboardSource>(new CommandDefinition("""
            SELECT Id, PublicId, AppId, Name, Definition
            FROM   meta.Page
            WHERE  PublicId = @pagePublicId AND PageType = 'Dashboard' AND IsDeleted = 0
            """, new { pagePublicId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<DashboardSource>> ListDashboardSourcesAsync(long appId, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        var pages = await connection.QueryAsync<DashboardSource>(new CommandDefinition("""
            SELECT Id, PublicId, AppId, Name, Definition
            FROM   meta.Page
            WHERE  AppId = @appId AND PageType = 'Dashboard' AND IsDeleted = 0
            """, new { appId }, cancellationToken: ct));
        return pages.ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, long>> GetReportTableIdsAsync(IReadOnlyCollection<Guid> reportPublicIds, CancellationToken ct = default)
    {
        if (reportPublicIds.Count == 0) return new Dictionary<Guid, long>();
        await using var connection = await OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<(Guid PublicId, long AppTableId)>(new CommandDefinition(
            "SELECT PublicId, AppTableId FROM meta.Report WHERE PublicId IN @reportPublicIds AND IsDeleted = 0",
            new { reportPublicIds }, cancellationToken: ct));
        return rows.ToDictionary(r => r.PublicId, r => r.AppTableId);
    }

    public async Task<long?> GetRoleIdAsync(Guid rolePublicId, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT Id FROM meta.AppRole WHERE PublicId = @rolePublicId AND IsDeleted = 0",
            new { rolePublicId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<CustomRuleSource>> ListCustomRulesOfRoleAsync(long roleId, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<CustomRuleSource>(new CommandDefinition("""
            SELECT AppRoleId AS RoleId, AppTableId AS TableId, Kind, FilterJson
            FROM   meta.AppRoleRecordFilter
            WHERE  AppRoleId = @roleId
            """, new { roleId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<CustomRuleSource>> ListCustomRulesOfTableAsync(long tableId, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<CustomRuleSource>(new CommandDefinition("""
            SELECT rf.AppRoleId AS RoleId, rf.AppTableId AS TableId, rf.Kind, rf.FilterJson
            FROM   meta.AppRoleRecordFilter rf
            JOIN   meta.AppRole r ON r.Id = rf.AppRoleId AND r.IsDeleted = 0
            WHERE  rf.AppTableId = @tableId
            """, new { tableId }, cancellationToken: ct));
        return rows.ToList();
    }

    private const string RelationshipSourceColumns =
        "Id, PublicId, ParentTableId, ChildTableId, ReferenceFieldId, ProxyFieldId, DisplayKeyFieldId";

    public async Task<RelationshipSource?> GetRelationshipSourceAsync(Guid relationshipPublicId, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<RelationshipSource>(new CommandDefinition(
            $"SELECT {RelationshipSourceColumns} FROM meta.Relationship WHERE PublicId = @relationshipPublicId AND IsDeleted = 0",
            new { relationshipPublicId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<RelationshipSource>> ListRelationshipsOfChildTableAsync(long childTableId, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<RelationshipSource>(new CommandDefinition(
            $"SELECT {RelationshipSourceColumns} FROM meta.Relationship WHERE ChildTableId = @childTableId AND IsDeleted = 0",
            new { childTableId }, cancellationToken: ct));
        return rows.ToList();
    }

    // ── PowerFlows ──────────────────────────────────────────────────────────────────────────────

    private sealed class PipelineStepRow
    {
        public long PipelineId { get; set; }
        public string RefId { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Subtype { get; set; } = string.Empty;
        public string? ConfigJson { get; set; }
    }

    private sealed class PipelineHeadRow
    {
        public long Id { get; set; }
        public Guid PublicId { get; set; }
        public long AppId { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private async Task<IReadOnlyList<PipelineSource>> LoadPipelinesAsync(string where, object parameters, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);
        // The where clause is one of this class's own constants, never user input.
        var heads = (await connection.QueryAsync<PipelineHeadRow>(new CommandDefinition(
            $"SELECT p.Id, p.PublicId, p.AppId, p.Name FROM meta.Pipeline p WHERE p.IsDeleted = 0 AND {where}", parameters, cancellationToken: ct))).ToList();
        if (heads.Count == 0) return [];

        var steps = (await connection.QueryAsync<PipelineStepRow>(new CommandDefinition("""
            SELECT s.PipelineId, s.RefId, s.Label, s.Type, s.Subtype, s.ConfigJson
            FROM   meta.PipelineStep s
            WHERE  s.IsDeleted = 0 AND s.PipelineId IN @ids
            ORDER BY s.PipelineId, s.DisplayOrder
            """, new { ids = heads.Select(h => h.Id).ToList() }, cancellationToken: ct))).ToLookup(s => s.PipelineId);

        return heads.Select(h => new PipelineSource(h.Id, h.PublicId, h.AppId, h.Name,
            steps[h.Id].Select(s => new PipelineStepSnapshot(s.RefId, s.Label, s.Type, s.Subtype, s.ConfigJson)).ToList())).ToList();
    }

    public async Task<PipelineSource?> GetPipelineSourceAsync(Guid pipelinePublicId, CancellationToken ct = default) =>
        (await LoadPipelinesAsync("p.PublicId = @pipelinePublicId", new { pipelinePublicId }, ct)).FirstOrDefault();

    public Task<IReadOnlyList<PipelineSource>> ListPipelineSourcesAsync(long appId, CancellationToken ct = default) =>
        LoadPipelinesAsync("p.AppId = @appId", new { appId }, ct);

    public async Task<(IReadOnlyList<Guid> Changed, DateTime ScannedUpTo)> ListPipelinesChangedSinceAsync(DateTime? since, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        // Read the clock first: anything saved after this point is at or after the next scan's starting point.
        var scannedUpTo = await connection.ExecuteScalarAsync<DateTime>(new CommandDefinition("SELECT SYSUTCDATETIME()", cancellationToken: ct));
        // Deleted pipelines are included on purpose: re-indexing one removes its rows.
        var ids = await connection.QueryAsync<Guid>(new CommandDefinition("""
            SELECT p.PublicId
            FROM   meta.Pipeline p
            WHERE  @since IS NULL
               OR  p.CreatedOn >= @since OR p.ModifiedOn >= @since OR p.DeletedOn >= @since
               OR  EXISTS (SELECT 1 FROM meta.PipelineStep s
                           WHERE s.PipelineId = p.Id
                             AND (s.CreatedOn >= @since OR s.ModifiedOn >= @since OR s.DeletedOn >= @since))
            """, new { since }, cancellationToken: ct));
        return (ids.ToList(), scannedUpTo);
    }

    public async Task<ImportDefinition?> GetImportDefinitionAsync(Guid importPublicId, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<ImportDefinition>(new CommandDefinition(
            "SELECT * FROM meta.ImportDefinition WHERE PublicId = @importPublicId AND IsDeleted = 0", new { importPublicId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ImportDefinition>> ListImportDefinitionsAsync(long appId, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        return (await connection.QueryAsync<ImportDefinition>(new CommandDefinition(
            "SELECT * FROM meta.ImportDefinition WHERE AppId = @appId AND IsDeleted = 0", new { appId }, cancellationToken: ct))).ToList();
    }

    public async Task<DateTime?> GetPipelineSyncWatermarkAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT SyncedUpTo FROM meta.FieldReferencePipelineSync WHERE Id = 1", cancellationToken: ct));
    }

    public async Task SetPipelineSyncWatermarkAsync(DateTime syncedUpTo, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE meta.FieldReferencePipelineSync SET SyncedUpTo = @syncedUpTo WHERE Id = 1;
            IF @@ROWCOUNT = 0 INSERT INTO meta.FieldReferencePipelineSync (Id, SyncedUpTo) VALUES (1, @syncedUpTo);
            """, new { syncedUpTo }, cancellationToken: ct));
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
