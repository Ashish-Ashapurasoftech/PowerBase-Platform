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

    /// <summary>
    /// Sends a switched-on field back to the build queue with a new version: whatever runs for the old version stops at the version fence,
    /// reads use the dynamic path until the new build is Ready. Used when a whole big table must be recomputed: a build splits it across
    /// sessions and resumes after a restart, a drain would do it in one session (hours on UAT, holding its slot).
    /// </summary>
    public async Task<bool> RequeueForRebuildAsync(long fieldId, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        var changed = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE meta.AppField SET MaterializeState = @none, MaterializeVersion = MaterializeVersion + 1
            WHERE Id = @fieldId AND IsDeleted = 0 AND MaterializeMode = @on AND MaterializeState IN (@building, @ready)
            """, new { fieldId, none = MaterializeStates.None, on = MaterializeModes.On, building = MaterializeStates.Building, ready = MaterializeStates.Ready },
            cancellationToken: ct)) == 1;
        if (changed) await ClearCheckpointAsync(fieldId, ct);
        return changed;
    }

    /// <summary>Building -> Ready in one statement, only if nobody changed the version meanwhile (the version fence).</summary>
    public async Task<bool> MarkReadyAsync(long fieldId, int version, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        var ready = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE meta.AppField SET MaterializeState = @ready, MaterializedOn = SYSUTCDATETIME()
            WHERE Id = @fieldId AND MaterializeVersion = @version AND MaterializeMode = @on AND MaterializeState = @building
            """, new { fieldId, version, ready = MaterializeStates.Ready, on = MaterializeModes.On, building = MaterializeStates.Building },
            cancellationToken: ct)) == 1;
        if (ready) await ClearCheckpointAsync(fieldId, ct);
        return ready;
    }

    /// <summary>The nightly check just made the field's values current (a Ready field is not checked again within ReconcileMinAgeHours).</summary>
    public async Task TouchMaterializedAsync(long fieldId, int version, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE meta.AppField SET MaterializedOn = SYSUTCDATETIME()
            WHERE Id = @fieldId AND MaterializeVersion = @version AND MaterializeMode = @on AND MaterializeState = @ready
            """, new { fieldId, version, on = MaterializeModes.On, ready = MaterializeStates.Ready }, cancellationToken: ct));
    }

    /// <summary>A build that cannot finish: reads keep today's path, nothing is retried until someone resets the field.</summary>
    public async Task MarkFailedAsync(long fieldId, int version, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE meta.AppField SET MaterializeState = @failed
            WHERE Id = @fieldId AND MaterializeVersion = @version AND MaterializeMode = @on
            """, new { fieldId, version, failed = MaterializeStates.Failed, on = MaterializeModes.On }, cancellationToken: ct));
        await ClearCheckpointAsync(fieldId, ct);
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

    // ── build checkpoints (migration 100): a first build continues where it stopped ─────────────────────

    /// <summary>One Id range of a field build and how far it got (see meta.ComputeBuildCheckpoint).</summary>
    public sealed record BuildSlice(long After, long UpTo, long DoneThroughId, long RowsWritten, long Watermark, bool IsDone);

    /// <summary>The slices a build of this field and version already recorded; empty for a build that never started (or an old database).</summary>
    public async Task<IReadOnlyList<BuildSlice>> LoadCheckpointAsync(long fieldId, int version, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        try
        {
            return (await connection.QueryAsync<BuildSlice>(new CommandDefinition("""
                SELECT SliceAfter AS After, SliceUpTo AS UpTo, DoneThroughId, RowsWritten, Watermark, IsDone
                FROM meta.ComputeBuildCheckpoint WHERE FieldId = @fieldId AND Version = @version ORDER BY SliceAfter
                """, new { fieldId, version }, cancellationToken: ct))).AsList();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return []; }   // migration 100 not applied yet: no resume
    }

    /// <summary>Records the slices of a fresh build (each starting at its own After). Existing rows are kept (another replica got there first).</summary>
    public async Task SaveCheckpointAsync(long fieldId, int version, IReadOnlyList<(long After, long UpTo)> slices, long watermark, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        try
        {
            // Older versions of the same field are dead (a new version restarts the build): clear them first.
            await connection.ExecuteAsync(new CommandDefinition("DELETE FROM meta.ComputeBuildCheckpoint WHERE FieldId = @fieldId AND Version <> @version",
                new { fieldId, version }, cancellationToken: ct));
            foreach (var (after, upTo) in slices)
                await connection.ExecuteAsync(new CommandDefinition("""
                    IF NOT EXISTS (SELECT 1 FROM meta.ComputeBuildCheckpoint WHERE FieldId = @fieldId AND Version = @version AND SliceAfter = @after)
                        INSERT INTO meta.ComputeBuildCheckpoint (FieldId, Version, SliceAfter, SliceUpTo, DoneThroughId, Watermark)
                        VALUES (@fieldId, @version, @after, @upTo, @after, @watermark)
                    """, new { fieldId, version, after, upTo, watermark }, cancellationToken: ct));
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { /* migration 100 not applied: builds simply cannot resume */ }
    }

    /// <summary>Moves a slice forward: everything up to <paramref name="doneThroughId"/> is written. Never goes backwards.</summary>
    public async Task AdvanceCheckpointAsync(long fieldId, int version, long sliceAfter, long doneThroughId, long rowsWrittenDelta, bool isDone, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE meta.ComputeBuildCheckpoint
                SET DoneThroughId = CASE WHEN @done > DoneThroughId THEN @done ELSE DoneThroughId END,
                    RowsWritten = RowsWritten + @delta, IsDone = CASE WHEN @isDone = 1 THEN 1 ELSE IsDone END, UpdatedOn = SYSUTCDATETIME()
                WHERE FieldId = @fieldId AND Version = @version AND SliceAfter = @sliceAfter
                """, new { fieldId, version, sliceAfter, done = doneThroughId, delta = rowsWrittenDelta, isDone }, cancellationToken: ct));
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { }
    }

    /// <summary>The build is over (Ready, Failed, superseded, switched off): its progress is not needed any more.</summary>
    public async Task ClearCheckpointAsync(long fieldId, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        try { await connection.ExecuteAsync(new CommandDefinition("DELETE FROM meta.ComputeBuildCheckpoint WHERE FieldId = @fieldId", new { fieldId }, cancellationToken: ct)); }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { }
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
