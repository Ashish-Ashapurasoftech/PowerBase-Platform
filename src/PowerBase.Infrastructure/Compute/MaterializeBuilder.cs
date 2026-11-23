using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Compute;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Repositories;

namespace PowerBase.Infrastructure.Compute;

/// <summary>
/// The full builds of stored columns (design 4.2): first fill of a switched-on field (backfill) and the nightly reconciliation sweep.
/// Build: rules -> c_ column (schema engine) -> Building (version fixed) -> every row recomputed in batches -> replay of the changes that
/// arrived meanwhile (outbox up to the watermark) -> Ready in one guarded statement. On error: Failed, reads keep today's path.
/// </summary>
public sealed class MaterializeBuilder : TenantRepositoryBase
{
    private readonly ISchemaEngineService _schemaEngine;
    private readonly ComputeModelCache _models;
    private readonly ComputeTableProcessor _processor;
    private readonly ComputeSearchNotifier? _search;
    private readonly ILogger<MaterializeBuilder>? _logger;
    private readonly ComputeDecryptor? _decryptor;

    public MaterializeBuilder(ITenantConnectionFactory connectionFactory, IQueryContext queryContext, ISchemaEngineService schemaEngine,
        ComputeModelCache models, ComputeTableProcessor processor, ILogger<MaterializeBuilder>? logger = null, ComputeSearchNotifier? search = null,
        ComputeDecryptor? decryptor = null)
        : base(connectionFactory, queryContext)
    {
        _search = search;
        _schemaEngine = schemaEngine;
        _models = models;
        _processor = processor;
        _logger = logger;
        _decryptor = decryptor;
    }

    public sealed record FieldOutcome(long FieldId, int Fid, long TableId, string Outcome, long Rows, long Differences, TimeSpan Took, string? Reason = null);

    private ComputeSchemaRepository Repo => new(ConnectionFactory, QueryContext);

    private const string UndecidedSql = """
        SELECT af.Id, ft.Code AS TypeCode
        FROM meta.AppField af
        JOIN core.FieldType ft ON ft.Id = af.FieldTypeId
        WHERE af.MaterializeMode = 0 AND af.IsDeleted = 0 AND af.Fid IS NOT NULL
        """;

    /// <summary>
    /// Decision Q3: every Lookup / Summary / Formula field is stored. Switches on each one not decided yet (MaterializeMode 0, which new
    /// fields start with); the build then applies the rules and marks the ones that cannot be stored Not allowed. Excluded fields (an
    /// admin's choice) and Not allowed ones are left alone here. Returns how many were switched on.
    /// </summary>
    public async Task<int> SwitchOnUndecidedAsync(CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var ids = (await conn.QueryAsync<(long Id, string TypeCode)>(new CommandDefinition(UndecidedSql, cancellationToken: ct)))
            .Where(f => PhysicalNaming.IsMaterializableTypeCode(f.TypeCode)).Select(f => f.Id).ToList();
        if (ids.Count == 0) return 0;
        var changed = 0;
        foreach (var chunk in ids.Chunk(1000))
            changed += await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE meta.AppField SET MaterializeMode = 1, MaterializeState = 0 WHERE Id IN @chunk AND MaterializeMode = 0 AND IsDeleted = 0",
                new { chunk }, cancellationToken: ct));
        if (changed > 0) _models.Invalidate(QueryContext.TenantId);
        return changed;
    }

    /// <summary>
    /// Nightly (decision Q3): a field the rules refused earlier gets another look — its inputs may have changed (encryption removed, a
    /// Today() taken out of a formula it reads). The ones the rules allow now are switched on and built.
    /// </summary>
    public async Task<int> RecheckNotAllowedAsync(CancellationToken ct)
    {
        var model = await _models.GetAsync(QueryContext.TenantId, Repo, fresh: true, ct);
        var ids = model.Decisions.Values
            .Where(d => d.Field.MaterializeMode == MaterializeModes.NotAllowed && d.Decision.Outcome == MaterializeOutcome.Allowed)
            .Select(d => d.Field.Id).ToList();
        if (ids.Count == 0) return 0;
        await using var conn = await OpenConnectionAsync(ct);
        var changed = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE meta.AppField SET MaterializeMode = 1, MaterializeState = 0 WHERE Id IN @ids AND MaterializeMode = 2 AND IsDeleted = 0",
            new { ids }, cancellationToken: ct));
        if (changed > 0) _models.Invalidate(QueryContext.TenantId);
        return changed;
    }

    /// <summary>Builds every switched-on field of the current tenant that is not built yet (None or a Building that stopped half way).</summary>
    public async Task<IReadOnlyList<FieldOutcome>> BuildPendingAsync(ComputeOptions options, CancellationToken ct)
    {
        var model = await _models.GetAsync(QueryContext.TenantId, Repo, fresh: true, ct);
        var outcomes = new List<FieldOutcome>();
        // Inputs before what reads them (a chain), so one round can build "Sum of Hours" and then "Sum of Sum of Hours".
        var order = model.Graph.TopologicalOrder.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i);
        var readyNow = model.Decisions.Where(d => d.Value.Field.MaterializeState == MaterializeStates.Ready && d.Value.Field.MaterializeMode == MaterializeModes.On)
            .Select(d => d.Key).ToHashSet();
        foreach (var (key, (field, decision)) in model.Decisions
                     .Where(d => d.Value.Field.MaterializeMode == MaterializeModes.On && d.Value.Field.MaterializeState is MaterializeStates.None or MaterializeStates.Building)
                     .OrderBy(d => order.TryGetValue(d.Key, out var i) ? i : int.MaxValue).ThenBy(d => d.Value.Field.Id))
        {
            ct.ThrowIfCancellationRequested();
            if (decision.Plan is { } p && StoredInputs(p).FirstOrDefault(k => !readyNow.Contains(k)) is { TableId: > 0 } waiting)
            {
                outcomes.Add(new(field.Id, field.Fid ?? 0, field.AppTableId, "waiting", 0, 0, TimeSpan.Zero,
                    $"Waiting for the stored field it reads ({waiting}) to be ready."));
                continue;
            }
            outcomes.Add(await BuildAsync(field, decision, model, options, ct));
            if (outcomes[^1].Outcome == "ready") readyNow.Add(key);
            if (outcomes[^1].Outcome == "busy") break;   // the database is busy: stop for now, the next poll continues
        }
        var dropped = await DropUnusedColumnsAsync(ct);
        if (outcomes.Count > 0 || dropped > 0) _models.Invalidate(QueryContext.TenantId);
        return outcomes;
    }

    /// <summary>
    /// Drops the stored columns of fields that were deleted or that the rules now refuse (Not allowed). Without this the old values stay in
    /// the table: for a field whose input became encrypted that is a plain-text copy of encrypted data. The column (with its indexes) is
    /// dropped first and the recorded name cleared after, so a crash in between is simply repeated on the next round.
    /// </summary>
    public async Task<int> DropUnusedColumnsAsync(CancellationToken ct)
    {
        var repo = Repo;
        var dropped = 0;
        foreach (var (id, tableId, fid) in await repo.ListColumnsToDropAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            try { await _schemaEngine.DropMaterializedColumnAsync(tableId, fid, ct); }
            catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { /* the table is gone: nothing to drop */ }
            await repo.ClearColumnNameAsync(id, ct);
            dropped++;
            _logger?.LogInformation("Compute: stored column c_{Fid} of t{TableId} (field {FieldId}) dropped: the field is deleted or can no longer be stored.", fid, tableId, id);
        }
        return dropped;
    }

    private async Task<FieldOutcome> BuildAsync(AppField field, MaterializeDecision decision, ComputeModel model, ComputeOptions options, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        FieldOutcome Done(string outcome, long rows = 0, string? reason = null) =>
            new(field.Id, field.Fid ?? 0, field.AppTableId, outcome, rows, 0, sw.Elapsed, reason);

        if (decision.Outcome == MaterializeOutcome.NotAllowed)
        {
            await Repo.MarkNotAllowedAsync(field.Id, ct);
            _logger?.LogInformation("Compute: field {FieldId} can never be stored ({Reason}); it stays on the dynamic path.", field.Id, decision.Reason);
            return Done("not-allowed", reason: decision.Reason);
        }
        if (decision.Outcome == MaterializeOutcome.NotYetSupported || decision.Plan is not { } plan)
            return Done("later", reason: decision.Reason);

        string? sqlType;
        await using (var probe = await OpenConnectionAsync(ct))
            sqlType = await MaterializedColumnWriter.StoredTypeAsync(probe, plan, ct);
        if (sqlType is null)
        {
            await Repo.MarkNotAllowedAsync(field.Id, ct);
            return Done("not-allowed", reason: "The value's type cannot be stored.");
        }
        if (!await _schemaEngine.EnsureMaterializedColumnAsync(plan.TableId, plan.Fid, sqlType, ct))
        {
            await Repo.MarkNotAllowedAsync(field.Id, ct);
            return Done("not-allowed", reason: "The table is too close to SQL Server's 1,024-column limit.");
        }

        var version = await Repo.StartBuildAsync(field.Id, ct);
        if (version is not int v) return Done("skipped", reason: "The field is no longer switched on.");

        long watermark;
        await using (var conn = await OpenConnectionAsync(ct))
            watermark = await conn.ExecuteScalarAsync<long>(new CommandDefinition("SELECT ISNULL(MAX(Id), 0) FROM meta.ComputeOutbox", cancellationToken: ct));

        try
        {
            MaterializedColumnWriter.WriteResult result;
            await using (var guard = await DatabaseGuard.AcquireAsync(ConnectionFactory.CreateAsync, options, backgroundOnly: true, TimeSpan.FromMinutes(2), _logger, ct))
            {
                if (guard is null) return Done("busy", reason: "No background compute slot was free.");
                result = await MaterializedColumnWriter.RecomputeAsync(guard, plan, sqlType, v, new WriteScope.All(), model.Schema, options, reconcile: false, ct, _decryptor);
                await ComputeTableProcessor.PropagateAsync(guard.Connection, model, plan, result, ct);
            }

            // Replay: changes saved while the build ran are in the outbox; everything up to the watermark must be applied before Ready.
            var inputTables = InputTables(plan);
            foreach (var table in inputTables) await _processor.DrainAsync(table, options, ct);
            if (await HasRowsUpToAsync(inputTables, watermark, ct))
                return Done("building", result.RowsWritten, "Changes from before the build are still waiting; it is finished on the next round.");

            // Design section 5: a unique formula is enforced by a filtered UNIQUE index on c_. Values that already collide refuse the build:
            // the field is Failed (old behaviour kept) and the clashing values are reported, never silently dropped.
            if (plan is FormulaPlan formula)
            {
                if (formula.IsUnique)
                {
                    var duplicates = await _schemaEngine.EnsureMaterializedUniqueIndexAsync(plan.TableId, plan.Fid, ct);
                    if (duplicates.Count > 0)
                    {
                        await Repo.MarkFailedAsync(field.Id, v, ct);
                        var list = string.Join(", ", duplicates.Select(d => $"'{d}'"));
                        _logger?.LogWarning("Compute: unique formula field {FieldId} has duplicate values ({Duplicates}); it is marked Failed until they are fixed.", field.Id, list);
                        return Done("failed", result.RowsWritten, $"Duplicate values: {list}");
                    }
                }
                else await _schemaEngine.DropMaterializedUniqueIndexAsync(plan.TableId, plan.Fid, ct);
            }

            if (!await Repo.MarkReadyAsync(field.Id, v, ct)) return Done("superseded", result.RowsWritten, "The field changed during the build.");
            // Decision Q7: the whole field goes into the search index (one field re-index job), now that it can be read from c_.
            if (_search is not null) await _search.ValuesChangedAsync(field, [], tooMany: true, ct);
            _logger?.LogInformation("Compute: field {FieldId} (t{TableId}.c_{Fid}) built: {Rows} row(s) in {Batches} batch(es), {Seconds:0.0}s. Ready.",
                field.Id, plan.TableId, plan.Fid, result.RowsWritten, result.Batches, sw.Elapsed.TotalSeconds);
            return Done("ready", result.RowsWritten);
        }
        catch (DatabaseBusyException ex)
        {
            return Done("busy", reason: ex.Message);   // stays Building; continued on the next poll
        }
        catch (MaterializeVersionChangedException)
        {
            return Done("superseded", reason: "The field changed during the build.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await Repo.MarkFailedAsync(field.Id, v, ct);
            _logger?.LogError(ex, "Compute: building field {FieldId} failed; it is marked Failed and reads keep the dynamic path.", field.Id);
            return Done("failed", reason: ex.Message);
        }
    }

    /// <summary>
    /// The nightly check (design 6.5): every Ready field is compared, batch by batch, with a fresh computation; a batch with differences is
    /// rewritten. Catches any write path that skipped the outbox. Reports the differences per field.
    /// </summary>
    public async Task<IReadOnlyList<FieldOutcome>> ReconcileAsync(ComputeOptions options, CancellationToken ct)
    {
        var model = await _models.GetAsync(QueryContext.TenantId, Repo, fresh: true, ct);
        var outcomes = new List<FieldOutcome>();
        foreach (var (field, plan) in model.Live().Where(l => l.Field.MaterializeState == MaterializeStates.Ready).OrderBy(l => l.Field.Id))
        {
            var sw = Stopwatch.StartNew();
            await using var guard = await DatabaseGuard.AcquireAsync(ConnectionFactory.CreateAsync, options, backgroundOnly: true, TimeSpan.FromMinutes(2), _logger, ct);
            if (guard is null) { outcomes.Add(new(field.Id, plan.Fid, plan.TableId, "busy", 0, 0, sw.Elapsed)); break; }

            var sqlType = await SqlColumnType.ReadAsync(guard.Connection, PhysicalNaming.FullTableName(plan.TableId), PhysicalNaming.MaterializedColumnName(plan.Fid), ct: ct);
            if (sqlType is null) { outcomes.Add(new(field.Id, plan.Fid, plan.TableId, "missing-column", 0, 0, sw.Elapsed)); continue; }
            try
            {
                var r = await MaterializedColumnWriter.RecomputeAsync(guard, plan, sqlType, field.MaterializeVersion, new WriteScope.All(), model.Schema, options, reconcile: true, ct, _decryptor);
                await ComputeTableProcessor.PropagateAsync(guard.Connection, model, plan, r, ct);   // fixed values feed their stored dependents
                outcomes.Add(new(field.Id, plan.Fid, plan.TableId, "checked", r.RowsWritten, r.Differences, sw.Elapsed));
                var level = r.Differences > 0 ? LogLevel.Warning : LogLevel.Information;
                _logger?.Log(level, "Compute reconciliation: tenant {TenantId} field {FieldId} (t{TableId}.c_{Fid}): {Differences} difference(s) fixed in {Batches} batch(es), {Seconds:0.0}s.",
                    QueryContext.TenantId, field.Id, plan.TableId, plan.Fid, r.Differences, r.Batches, sw.Elapsed.TotalSeconds);
            }
            catch (DatabaseBusyException) { outcomes.Add(new(field.Id, plan.Fid, plan.TableId, "busy", 0, 0, sw.Elapsed)); break; }
            catch (MaterializeVersionChangedException) { outcomes.Add(new(field.Id, plan.Fid, plan.TableId, "superseded", 0, 0, sw.Elapsed)); }
        }
        return outcomes;
    }

    /// <summary>The stored calculated fields the plan reads (a chain): they must be Ready before it is built.</summary>
    internal static IReadOnlyList<FieldKey> StoredInputs(MaterializePlan plan) => plan switch
    {
        SummaryPlan { TargetIsStored: true, TargetFid: int t } s => [new FieldKey(s.ChildTableId, t)],
        LookupPlan l when l.SourceColumn.StartsWith("c_", StringComparison.Ordinal) => [new FieldKey(l.ParentTableId, l.SourceFid)],
        FormulaPlan f => f.StoredReads.Select(r => new FieldKey(f.TableId, r)).ToList(),
        _ => []
    };

    internal static IReadOnlyList<long> InputTables(MaterializePlan plan) => plan switch
    {
        SummaryPlan s => new[] { s.ChildTableId, s.ParentTableId }.Distinct().ToList(),
        LookupPlan l => new[] { l.ParentTableId, l.ChildTableId }.Distinct().ToList(),
        _ => [plan.TableId]
    };

    private async Task<bool> HasRowsUpToAsync(IReadOnlyList<long> tables, long watermark, CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM meta.ComputeOutbox WHERE Id <= @watermark AND TableId IN @tables) THEN 1 ELSE 0 END",
            new { watermark, tables }, cancellationToken: ct)) == 1;
    }
}
