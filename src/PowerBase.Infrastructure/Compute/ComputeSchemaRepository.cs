using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Compute;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Repositories;

namespace PowerBase.Infrastructure.Compute;

/// <summary>
/// Field metadata and the stored-column lifecycle (meta.AppField.Materialize*) for the compute worker. Always the PRIMARY: metadata
/// is never read from a replica. Every state change is one guarded UPDATE, so two workers can never both move the same field.
/// </summary>
public sealed class ComputeSchemaRepository : TenantRepositoryBase
{
    public ComputeSchemaRepository(ITenantConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    private const string FieldsSql = """
        SELECT af.Id, af.AppTableId, ft.Code AS TypeCode, af.Fid, af.Name, af.Label, af.Settings, af.IsEncrypted, af.IsSystem, af.PhysicalColumnName,
               af.IsUnique, af.IsSearchable, af.IsFilterable,
               af.MaterializeMode, af.MaterializeState, af.MaterializeVersion, af.MaterializedOn
        FROM meta.AppField af
        JOIN core.FieldType ft ON ft.Id = af.FieldTypeId
        JOIN meta.AppTable t ON t.Id = af.AppTableId AND t.IsDeleted = 0
        JOIN meta.App a ON a.Id = t.AppId AND a.IsDeleted = 0   -- a deleted app keeps its tables "live": its fields are never built
        WHERE af.IsDeleted = 0;

        SELECT t.Id AS TableId, t.AppId, CAST(a.IsEncrypted AS BIT) AS IsEncrypted
        FROM meta.AppTable t
        JOIN meta.App a ON a.Id = t.AppId AND a.IsDeleted = 0
        WHERE t.IsDeleted = 0;
        """;

    public async Task<ComputeSchema> LoadAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition(FieldsSql, cancellationToken: ct));
        var fields = (await grid.ReadAsync<AppField>()).AsList();
        var tables = (await grid.ReadAsync<(long TableId, long AppId, bool IsEncrypted)>()).AsList();
        return new ComputeSchema(fields,
            tables.ToDictionary(t => t.TableId, t => t.AppId),
            tables.Where(t => t.IsEncrypted).Select(t => t.AppId).ToHashSet());
    }

    /// <summary>
    /// A cheap fingerprint of every field that is switched on or has a stored-column state (mode, state, version, settings). Read on every
    /// live round: when it differs from the cached model's, the model is reloaded. That is what guarantees a field that just moved to
    /// Building is seen by every worker replica before it applies (and deletes) the next outbox rows.
    /// </summary>
    public async Task<long> FingerprintAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
            SELECT ISNULL(CAST(CHECKSUM_AGG(CHECKSUM(af.Id, af.MaterializeMode, af.MaterializeState, af.MaterializeVersion, af.Settings, af.IsDeleted)) AS BIGINT), 0) * 100000
                   + COUNT_BIG(*)
            FROM meta.AppField af
            WHERE af.MaterializeMode <> 0 OR af.MaterializeState <> 0
            """, cancellationToken: ct));
    }

    /// <summary>
    /// Moves a switched-on field to Building and returns the version the build writes for. From None the version is bumped (a fresh build);
    /// a field already Building (a build that stopped half way) keeps its version and is simply built again (recompute, never increment).
    /// Null when the field is no longer switched on or is already Ready / Failed.
    /// </summary>
    public async Task<int?> StartBuildAsync(long fieldId, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<int?>(new CommandDefinition("""
            UPDATE meta.AppField
            SET MaterializeVersion = MaterializeVersion + CASE WHEN MaterializeState = @none THEN 1 ELSE 0 END,
                MaterializeState = @building,
                MaterializedColumnName = CONCAT('c_', Fid)   -- the column EnsureMaterializedColumnAsync just made; recorded like PhysicalColumnName is for f_
            OUTPUT inserted.MaterializeVersion
            WHERE Id = @fieldId AND IsDeleted = 0 AND MaterializeMode = @on AND MaterializeState IN (@none, @building)
            """, new { fieldId, none = MaterializeStates.None, building = MaterializeStates.Building, on = MaterializeModes.On }, cancellationToken: ct));
    }

    /// <summary>Building -> Ready in one statement, only if nobody changed the version meanwhile (the version fence).</summary>
    public async Task<bool> MarkReadyAsync(long fieldId, int version, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE meta.AppField SET MaterializeState = @ready, MaterializedOn = SYSUTCDATETIME()
            WHERE Id = @fieldId AND MaterializeVersion = @version AND MaterializeMode = @on AND MaterializeState = @building
            """, new { fieldId, version, ready = MaterializeStates.Ready, on = MaterializeModes.On, building = MaterializeStates.Building },
            cancellationToken: ct)) == 1;
    }

    /// <summary>A build that cannot finish: reads keep today's path, nothing is retried until someone resets the field.</summary>
    public async Task MarkFailedAsync(long fieldId, int version, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE meta.AppField SET MaterializeState = @failed
            WHERE Id = @fieldId AND MaterializeVersion = @version AND MaterializeMode = @on
            """, new { fieldId, version, failed = MaterializeStates.Failed, on = MaterializeModes.On }, cancellationToken: ct));
    }

    /// <summary>
    /// Stored columns that must not exist any more: the field was deleted, or the rules refuse it (Not allowed — an input became encrypted,
    /// a Today() was added…). A refused field keeps its old c_ values otherwise, and values derived from an encrypted field must never stay
    /// in plain text (design 8.2). MaterializedColumnName is set when a column was built and cleared once it is dropped.
    /// </summary>
    public async Task<IReadOnlyList<(long Id, long AppTableId, int Fid)>> ListColumnsToDropAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        return (await connection.QueryAsync<(long Id, long AppTableId, int Fid)>(new CommandDefinition("""
            SELECT Id, AppTableId, Fid FROM meta.AppField
            WHERE MaterializedColumnName IS NOT NULL AND Fid IS NOT NULL AND (IsDeleted = 1 OR MaterializeMode = @notAllowed)
            """, new { notAllowed = MaterializeModes.NotAllowed }, cancellationToken: ct))).AsList();
    }

    /// <summary>The stored column of this field has been dropped.</summary>
    public async Task ClearColumnNameAsync(long fieldId, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE meta.AppField SET MaterializedColumnName = NULL, MaterializedOn = NULL WHERE Id = @fieldId", new { fieldId }, cancellationToken: ct));
    }

    /// <summary>The field can never be stored (MaterializeMode = 2): it stays on today's dynamic path for good.</summary>
    public async Task MarkNotAllowedAsync(long fieldId, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE meta.AppField SET MaterializeMode = @notAllowed, MaterializeState = @none, MaterializeVersion = MaterializeVersion + 1
            WHERE Id = @fieldId AND MaterializeMode = @on
            """, new { fieldId, notAllowed = MaterializeModes.NotAllowed, none = MaterializeStates.None, on = MaterializeModes.On }, cancellationToken: ct));
    }
}
