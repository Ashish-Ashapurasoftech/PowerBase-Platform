using Dapper;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

/// <summary>See <see cref="ISearchIndexStates"/>. Always the primary database: these are metadata writes.</summary>
public sealed class SearchIndexStateRepository : TenantRepositoryBase, ISearchIndexStates
{
    /// <summary>The round-trip scan reads one column of the whole table; a big table may take a while, but it runs once per field job.</summary>
    private const int ExactCheckTimeoutSeconds = 600;

    private readonly ILogger<SearchIndexStateRepository>? _logger;

    public SearchIndexStateRepository(ITenantConnectionFactory connectionFactory, IQueryContext queryContext, ILogger<SearchIndexStateRepository>? logger = null)
        : base(connectionFactory, queryContext)
    {
        _logger = logger;
    }

    // Moves only the field waiting for this very job: Indexing, Ready, sent with the version now built, and sent no later than the job was
    // created. SearchExact starts at 0 (sort and shown value on SQL) until CheckExactAsync has looked at the values.
    private const string MarkIndexedSql = """
        UPDATE af
        SET SearchIndexState = @indexed, SearchIndexedOn = SYSUTCDATETIME(), SearchExact = 0
        OUTPUT inserted.Id AS FieldId, inserted.AppTableId AS TableId, inserted.Fid, inserted.SearchIndexVersion AS Version
        FROM meta.AppField af
        JOIN meta.SearchFieldJob j ON j.Id = @jobId AND j.AfterId = @fromAfterId AND j.CreatedOn >= af.SearchIndexRequestedOn
        WHERE af.AppTableId = @tableId AND af.Fid = @fid AND af.IsDeleted = 0
          AND af.MaterializeMode = @on AND af.MaterializeState = @ready
          AND af.SearchIndexState = @indexing AND af.SearchIndexVersion = af.MaterializeVersion
        """;

    private const string SetExactSql = """
        UPDATE meta.AppField SET SearchExact = @exact
        WHERE Id = @fieldId AND IsDeleted = 0 AND SearchIndexState = @indexed AND SearchIndexVersion = @version AND MaterializeVersion = @version
        """;

    private const string MarkNotIndexedSql = """
        UPDATE af
        SET SearchIndexState = @failed, SearchIndexedOn = NULL
        FROM meta.AppField af
        JOIN meta.SearchFieldJob j ON j.Id = @jobId AND j.CreatedOn >= af.SearchIndexRequestedOn
        WHERE af.AppTableId = @tableId AND af.Fid = @fid AND af.IsDeleted = 0
          AND af.SearchIndexState = @indexing AND af.SearchIndexVersion = af.MaterializeVersion
        """;

    // The SQL type of the stored column c_{fid}: only numbers can lose digits in the index.
    private const string ColumnTypeSql = """
        SELECT ty.name
        FROM sys.columns c
        JOIN sys.types ty ON ty.user_type_id = c.user_type_id
        WHERE c.object_id = OBJECT_ID(@table) AND c.name = @column
        """;

    private static readonly HashSet<string> NumericSqlTypes = new(StringComparer.OrdinalIgnoreCase)
        { "decimal", "numeric", "money", "smallmoney", "bigint", "int", "smallint", "tinyint", "float", "real" };

    public async Task<SearchIndexedField?> MarkIndexedAsync(long tableId, long fid, long jobId, long fromAfterId, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        var field = await connection.QuerySingleOrDefaultAsync<SearchIndexedField>(new CommandDefinition(MarkIndexedSql, new
        {
            tableId, fid, jobId, fromAfterId,
            on = MaterializeModes.On, ready = MaterializeStates.Ready,
            indexing = SearchIndexStates.Indexing, indexed = SearchIndexStates.Indexed
        }, cancellationToken: ct));
        if (field is not null)
            _logger?.LogInformation("Search: stored field {FieldId} (t{TableId}.c_{Fid}, version {Version}) is in the search index.",
                field.FieldId, tableId, fid, field.Version);
        return field;
    }

    public async Task<bool> CheckExactAsync(SearchIndexedField field, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        var exact = await IsExactAsync(connection, field.TableId, field.Fid, ct);
        await connection.ExecuteAsync(new CommandDefinition(SetExactSql,
            new { fieldId = field.FieldId, version = field.Version, exact, indexed = SearchIndexStates.Indexed }, cancellationToken: ct));
        if (!exact)
            _logger?.LogInformation("Search: stored field {FieldId} (t{TableId}.c_{Fid}) has values with more digits than the index keeps; its sort and shown values stay on SQL.",
                field.FieldId, field.TableId, field.Fid);
        return exact;
    }

    public async Task<bool> MarkNotIndexedAsync(long tableId, long fid, long jobId, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        var moved = await connection.ExecuteAsync(new CommandDefinition(MarkNotIndexedSql, new
        {
            tableId, fid, jobId, indexing = SearchIndexStates.Indexing, failed = SearchIndexStates.Failed
        }, cancellationToken: ct)) > 0;
        if (moved)
            _logger?.LogWarning("Search: stored field t{TableId}.c_{Fid} has no column in the search index; it is read from SQL.", tableId, fid);
        return moved;
    }

    /// <summary>
    /// True when every stored value comes back unchanged through a double. Not a number = exact. Any doubt (no column, the scan fails or runs
    /// too long) = not exact, which only keeps sort and the shown value on SQL.
    /// </summary>
    private async Task<bool> IsExactAsync(Microsoft.Data.SqlClient.SqlConnection connection, long tableId, int fid, CancellationToken ct)
    {
        var table = PhysicalNaming.FullTableName(tableId);
        var column = PhysicalNaming.MaterializedColumnName(fid);
        try
        {
            var type = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(ColumnTypeSql, new { table, column }, cancellationToken: ct));
            if (type is null) return false;
            if (!NumericSqlTypes.Contains(type)) return true;

            // Table and column names come from integer ids only (PhysicalNaming), never from user input.
            var sql = $"""
                SELECT CASE WHEN EXISTS (
                    SELECT 1 FROM {table}
                    WHERE IsDeleted = 0 AND {column} IS NOT NULL
                      AND CAST(CAST({column} AS FLOAT) AS DECIMAL(38, 8)) <> {column}) THEN 0 ELSE 1 END
                """;
            return await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, commandTimeout: ExactCheckTimeoutSeconds, cancellationToken: ct)) == 1;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Search: could not check the digits of t{TableId}.c_{Fid}; its sort and shown values stay on SQL.", tableId, fid);
            return false;
        }
    }
}
