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
    private readonly ProjectedValueComputer? _projector;

    public MaterializeBuilder(ITenantConnectionFactory connectionFactory, IQueryContext queryContext, ISchemaEngineService schemaEngine,
        ComputeModelCache models, ComputeTableProcessor processor, ILogger<MaterializeBuilder>? logger = null, ComputeSearchNotifier? search = null,
        ComputeDecryptor? decryptor = null, ProjectedValueComputer? projector = null)
        : base(connectionFactory, queryContext)
    {
        _search = search;
        _schemaEngine = schemaEngine;
        _models = models;
        _processor = processor;
        _logger = logger;
        _decryptor = decryptor;
        _projector = projector;
    }

    public sealed record FieldOutcome(long FieldId, int Fid, long TableId, string Outcome, long Rows, long Differences, TimeSpan Took, string? Reason = null);

    private ComputeSchemaRepository Repo => new(ConnectionFactory, QueryContext);

    private const string UndecidedSql = """
        SELECT af.Id, af.AppTableId AS TableId, ft.Code AS TypeCode
        FROM meta.AppField af
        JOIN core.FieldType ft ON ft.Id = af.FieldTypeId
        JOIN meta.AppTable t ON t.Id = af.AppTableId AND t.IsDeleted = 0
        JOIN meta.App a ON a.Id = t.AppId AND a.IsDeleted = 0
        WHERE af.MaterializeMode = 0 AND af.IsDeleted = 0 AND af.Fid IS NOT NULL
        """;

    /// <summary>
    /// Decision Q3: every Lookup / Summary / Formula field is stored. Switches on each one not decided yet (MaterializeMode 0, which new
    /// fields start with); the build then applies the rules and marks the ones that cannot be stored Not allowed. Excluded fields (an
    /// admin's choice) and Not allowed ones are left alone here. Returns how many were switched on.
    /// <paramref name="minRecords"/> (Compute:MinRecordsForStoredColumns): only fields of a table holding at least that many records are
    /// switched on — a small table is cheap to calculate on the fly, so it gets no stored column until it grows. 0 = every table.
    /// A field already switched on stays on when its table later shrinks.
    /// </summary>
    public async Task<int> SwitchOnUndecidedAsync(CancellationToken ct, long minRecords = 0)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var fields = (await conn.QueryAsync<(long Id, long TableId, string TypeCode)>(new CommandDefinition(UndecidedSql, cancellationToken: ct)))
            .Where(f => PhysicalNaming.IsMaterializableTypeCode(f.TypeCode)).ToList();
        if (fields.Count == 0) return 0;
        var big = await TablesWithAtLeastAsync(conn, fields.Select(f => f.TableId), minRecords, ct);
        var ids = fields.Where(f => big.Contains(f.TableId)).Select(f => f.Id).ToList();
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
    /// The tables (of <paramref name="tableIds"/>) that hold at least <paramref name="minRecords"/> records. The count is the clustered
    /// index / heap row_count from sys.dm_db_partition_stats: instant, no scan, and approximate (soft-deleted rows are still counted),
    /// which is enough for a threshold. 0 or less = every table.
    /// </summary>
    private static async Task<HashSet<long>> TablesWithAtLeastAsync(Microsoft.Data.SqlClient.SqlConnection conn, IEnumerable<long> tableIds,
        long minRecords, CancellationToken ct)
    {
        var all = tableIds.Distinct().ToList();
        if (minRecords <= 0 || all.Count == 0) return all.ToHashSet();
        var counts = await TableRowCountsAsync(conn, all, ct);
        return all.Where(id => counts.TryGetValue(id, out var rows) && rows >= minRecords).ToHashSet();
    }

    /// <summary>Approximate row count per table (partition statistics: instant, no scan). A table whose data.t_ table is missing is left out.</summary>
    private static async Task<Dictionary<long, long>> TableRowCountsAsync(Microsoft.Data.SqlClient.SqlConnection conn, IEnumerable<long> tableIds,
        CancellationToken ct)
    {
        var byName = tableIds.Distinct().ToDictionary(PhysicalNaming.TableName);
        var result = new Dictionary<long, long>();
        foreach (var chunk in byName.Keys.Chunk(1000))
        {
            var counts = await conn.QueryAsync<(string Name, long Rows)>(new CommandDefinition("""
                SELECT o.name AS Name, SUM(st.row_count) AS Rows
                FROM sys.dm_db_partition_stats st
                JOIN sys.objects o ON o.object_id = st.object_id
                JOIN sys.schemas s ON s.schema_id = o.schema_id AND s.name = 'data'
                WHERE st.index_id IN (0, 1) AND o.name IN @chunk
                GROUP BY o.name
                """, new { chunk }, cancellationToken: ct));
            foreach (var (name, rows) in counts) result[byName[name]] = rows;
        }
        return result;
    }

    /// <summary>
    /// True when the field's build belongs on the fast lane: every table it reads or writes holds fewer than <paramref name="maxRows"/> rows
    /// (a summary of a small parent over a big child table is big). A field with no plan (refused, not supported yet) costs nothing.
    /// A table missing from <paramref name="rowCounts"/> counts as empty.
    /// </summary>
    internal static bool IsFastLane(MaterializePlan? plan, IReadOnlyDictionary<long, long> rowCounts, long maxRows)
    {
        if (plan is null) return true;
        return LaneTables(plan).All(t => !rowCounts.TryGetValue(t, out var rows) || rows < maxRows);
    }

    private static IEnumerable<long> LaneTables(MaterializePlan plan) =>
        InputTables(plan).Append(plan.TableId).Append(MaterializedColumnWriter.ShapeOf(plan).TableId).Distinct();

    /// <summary>
    /// Splits the build list (already in priority order) into the fast lane (small tables) and the rest, keeping the order inside each.
    /// No reserved builds = one list, as before.
    /// </summary>
    internal static (List<T> Fast, List<T> Big) SplitLanes<T>(IEnumerable<T> ordered, Func<T, MaterializePlan?> planOf,
        IReadOnlyDictionary<long, long> rowCounts, ComputeOptions options)
    {
        var all = ordered.ToList();
        if (options.FastLaneReserved <= 0) return ([], all);
        var fast = new List<T>();
        var big = new List<T>();
        foreach (var item in all) (IsFastLane(planOf(item), rowCounts, options.FastLaneMaxRows) ? fast : big).Add(item);
        return (fast, big);
    }

    /// <summary>
    /// Nightly (decision Q3): a field the rules refused earlier gets another look — its inputs may have changed (encryption removed, a
    /// Today() taken out of a formula it reads). The ones the rules allow now are switched on and built.
    /// </summary>
    public async Task<int> RecheckNotAllowedAsync(CancellationToken ct, long minRecords = 0)
    {
        var model = await _models.GetAsync(QueryContext.TenantId, Repo, fresh: true, ct);
        var allowed = model.Decisions.Values
            .Where(d => d.Field.MaterializeMode == MaterializeModes.NotAllowed && d.Decision.Outcome == MaterializeOutcome.Allowed)
            .Select(d => d.Field).ToList();
        if (allowed.Count == 0) return 0;
        await using var conn = await OpenConnectionAsync(ct);
        var big = await TablesWithAtLeastAsync(conn, allowed.Select(f => f.AppTableId), minRecords, ct);
        var ids = allowed.Where(f => big.Contains(f.AppTableId)).Select(f => f.Id).ToList();
        if (ids.Count == 0) return 0;
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
        // The tables people open most first (Compute:PriorityTables), then inputs before what reads them.
        var priority = options.PriorityTablesFor(QueryContext.TenantId);
        // Up to BackgroundSessionsPerDatabase fields side by side, each on its own guarded connection (the guard holds the database to that
        // many background slots and slows or stops them when it is busy).
        var concurrency = options.BuildConcurrency;
        var onlyApps = options.BuildOnlyAppsFor(QueryContext.TenantId);   // Compute:BuildOnlyApps: null = every app
        var pending = model.Decisions
            .Where(d => d.Value.Field.MaterializeMode == MaterializeModes.On && d.Value.Field.MaterializeState is MaterializeStates.None or MaterializeStates.Building)
            .Where(d => onlyApps is null || model.Schema.AppIdOf(d.Key.TableId) is long app && onlyApps.Contains(app))
            .OrderBy(d => priority.TryGetValue(d.Key.TableId, out var rank) ? rank : int.MaxValue)
            .ThenBy(d => order.TryGetValue(d.Key, out var i) ? i : int.MaxValue).ThenBy(d => d.Value.Field.Id)
            .ToList();
        if (pending.Count == 0)
        {
            var droppedNow = await DropUnusedColumnsAsync(ct);
            if (droppedNow > 0) _models.Invalidate(QueryContext.TenantId);
            return outcomes;
        }

        // Fast lane: FastLaneReserved builds (and database background slots) are kept for fields of small tables, so a few builds of hours on
        // a big table can no longer hold up hundreds of fields that take a second each. A small field may use any free build; a big one
        // only the others. No reservation = one lane, exactly as before.
        var reserved = options.FastLaneReserved;
        var rowCounts = new Dictionary<long, long>();
        if (reserved > 0)
        {
            await using var conn = await OpenConnectionAsync(ct);
            rowCounts = await TableRowCountsAsync(conn, pending.Where(d => d.Value.Decision.Plan is not null)
                .SelectMany(d => LaneTables(d.Value.Decision.Plan!)), ct);
        }
        var (fastLane, bigLane) = SplitLanes(pending, d => d.Value.Decision.Plan, rowCounts, options);
        if (reserved > 0)
            _logger?.LogInformation("Compute build round: tenant {TenantId}: {Fast} field(s) of small tables on the fast lane ({Reserved} of {Builds} build(s) kept for them), {Big} on big tables.",
                QueryContext.TenantId, fastLane.Count, reserved, concurrency, bigLane.Count);

        using var gate = new SemaphoreSlim(concurrency);
        using var bigGate = new SemaphoreSlim(Math.Max(1, concurrency - reserved));
        var running = new List<Task>();
        var sync = new object();
        // A round stops starting builds after BuildRoundMinutes: the next one reads the waiting fields (and priorities) again.
        var roundEnds = DateTime.UtcNow.AddMinutes(options.BuildRoundMinutes);

        // One dispatcher per lane, side by side. "busy" (no slot, or the database stays loaded) stops that lane for this round.
        async Task DispatchAsync(List<KeyValuePair<FieldKey, (AppField Field, MaterializeDecision Decision)>> lane, bool isBig)
        {
            var busy = false;
            foreach (var (key, (field, decision)) in lane)
            {
                ct.ThrowIfCancellationRequested();
                if (DateTime.UtcNow > roundEnds) break;
                if (isBig) await bigGate.WaitAsync(ct);
                await gate.WaitAsync(ct);
                void Release() { gate.Release(); if (isBig) bigGate.Release(); }
                if (DateTime.UtcNow > roundEnds) { Release(); break; }   // waited for a free build past the end of the round
                FieldKey? waiting;
                bool stop;
                lock (sync)
                {
                    stop = busy;
                    waiting = stop ? null : decision.Plan is { } p ? StoredInputs(p).FirstOrDefault(k => !readyNow.Contains(k)) : null;
                    if (waiting is { TableId: > 0 } w)
                        outcomes.Add(new(field.Id, field.Fid ?? 0, field.AppTableId, "waiting", 0, 0, TimeSpan.Zero,
                            $"Waiting for the stored field it reads ({w}) to be ready."));
                }
                if (stop) { Release(); break; }   // the database is busy for this lane: stop for now, the next poll continues
                if (waiting is { TableId: > 0 }) { Release(); continue; }

                var task = Task.Run(async () =>
                {
                    try
                    {
                        var outcome = await BuildAsync(field, decision, model, options, ct, keepFree: isBig ? reserved : 0);
                        lock (sync)
                        {
                            outcomes.Add(outcome);
                            if (outcome.Outcome == "ready") readyNow.Add(key);
                            if (outcome.Outcome == "busy") busy = true;
                        }
                    }
                    finally { Release(); }
                }, ct);
                lock (sync) running.Add(task);
            }
        }

        await Task.WhenAll(DispatchAsync(fastLane, isBig: false), DispatchAsync(bigLane, isBig: true));
        Task[] started;
        lock (sync) started = running.ToArray();
        await Task.WhenAll(started);
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

    /// <param name="keepFree">Database background slots this build leaves to the fast lane (<see cref="ComputeOptions.FastLaneReserved"/>).</param>
    private async Task<FieldOutcome> BuildAsync(AppField field, MaterializeDecision decision, ComputeModel model, ComputeOptions options, CancellationToken ct,
        int keepFree = 0)
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

        // One replica per field: another one already on it means this one moves to the next field instead of doing it twice.
        await using var fieldLock = await FieldWorkLock.TryAcquireAsync(ConnectionFactory.CreateAsync, field.Id, ct);
        if (fieldLock is null) return Done("elsewhere", reason: "Another worker is building this field.");

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

        try
        {
            // Checkpoint: a build that was interrupted (restart, new revision, busy database) continues from the last Id it wrote, over the
            // same ranges and with the watermark of its first start; a fresh build records its ranges first. Every build is a list of Id
            // ranges (one range = one session), so the resumed and the fresh path are the same code.
            var recorded = await Repo.LoadCheckpointAsync(field.Id, v, ct);
            long watermark;
            long alreadyWritten = 0;
            List<BuildRange> work;
            if (recorded.Count > 0)
            {
                watermark = recorded.Min(r => r.Watermark);
                alreadyWritten = recorded.Sum(r => r.RowsWritten);
                work = recorded.Where(r => !r.IsDone).Select(r => new BuildRange(r.After, new WriteScope.Range(r.DoneThroughId, r.UpTo))).ToList();
                _logger?.LogInformation("Compute: field {FieldId} (t{TableId}.c_{Fid}) resumes its build: {Open} of {Total} range(s) left, {Rows} row(s) already written.",
                    field.Id, plan.TableId, plan.Fid, work.Count, recorded.Count, alreadyWritten);
            }
            else
            {
                await using (var conn = await OpenConnectionAsync(ct))
                    watermark = await conn.ExecuteScalarAsync<long>(new CommandDefinition("SELECT ISNULL(MAX(Id), 0) FROM meta.ComputeOutbox", cancellationToken: ct));
                var ranges = await SlicesAsync(plan, options, ct) ?? [new WriteScope.Range(0, long.MaxValue)];
                await Repo.SaveCheckpointAsync(field.Id, v, ranges.Select(r => (r.After, r.UpTo)).ToList(), watermark, ct);
                work = ranges.Select(r => new BuildRange(r.After, r)).ToList();
            }

            // Propagate only a REbuild (the field was Ready before, its values may change); a first build changes nothing anyone saw.
            var sliced = await BuildSlicedAsync(plan, sqlType, v, model, work, options, ct, keepFree, propagate: field.MaterializedOn is not null);
            if (sliced is null) return Done("busy", reason: "No background compute slot was free.");
            var (result, sessions) = sliced.Value;
            result = result with { RowsWritten = result.RowsWritten + alreadyWritten };

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
            _logger?.LogInformation("Compute: field {FieldId} (t{TableId}.c_{Fid}) built: {Rows} row(s) in {Batches} batch(es), {Seconds:0.0}s. Ready. " +
                "({Plan}, {Sessions} session(s), compute {ComputeSeconds:0.0}s, write {WriteSeconds:0.0}s summed over sessions)",
                field.Id, plan.TableId, plan.Fid, result.RowsWritten, result.Batches, sw.Elapsed.TotalSeconds,
                plan.GetType().Name, sessions, result.ComputeTime.TotalSeconds, result.WriteTime.TotalSeconds);
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
        // The check shares the background slots with the first builds. While fields are still waiting to be built (or half built) every
        // slot goes to them: a check re-reads whole tables of fields that were computed from current data hours ago, and a worker that
        // restarts runs it again at once. It follows when the backlog is empty (Compute:ReconcileWhileBuilding = true keeps the old behaviour).
        var backlog = model.Decisions.Values.Count(d => d.Field.MaterializeMode == MaterializeModes.On
            && d.Field.MaterializeState is MaterializeStates.None or MaterializeStates.Building && d.Decision.Outcome == MaterializeOutcome.Allowed);
        if (backlog > 0 && !options.ReconcileWhileBuilding)
        {
            _logger?.LogInformation("Compute reconciliation: tenant {TenantId} skipped, {Backlog} field(s) are still waiting for their first build.", QueryContext.TenantId, backlog);
            return outcomes;
        }
        var recent = DateTime.UtcNow.AddHours(-Math.Max(0, options.ReconcileMinAgeHours));
        foreach (var (field, plan) in model.Live().Where(l => l.Field.MaterializeState == MaterializeStates.Ready).OrderBy(l => l.Field.Id))
        {
            if (field.MaterializedOn is { } madeCurrent && madeCurrent > recent) continue;   // built or checked a few hours ago: nothing to catch yet
            var sw = Stopwatch.StartNew();
            await using var fieldLock = await FieldWorkLock.TryAcquireAsync(ConnectionFactory.CreateAsync, field.Id, ct);
            if (fieldLock is null) continue;   // another replica is checking (or rebuilding) it
            await using var guard = await DatabaseGuard.AcquireAsync(ConnectionFactory.CreateAsync, options, backgroundOnly: true, TimeSpan.FromMinutes(2), _logger, ct);
            if (guard is null) { outcomes.Add(new(field.Id, plan.Fid, plan.TableId, "busy", 0, 0, sw.Elapsed)); break; }

            var sqlType = await SqlColumnType.ReadAsync(guard.Connection, PhysicalNaming.FullTableName(plan.TableId), PhysicalNaming.MaterializedColumnName(plan.Fid), ct: ct);
            if (sqlType is null) { outcomes.Add(new(field.Id, plan.Fid, plan.TableId, "missing-column", 0, 0, sw.Elapsed)); continue; }
            try
            {
                var r = await MaterializedColumnWriter.RecomputeAsync(guard, plan, sqlType, field.MaterializeVersion, new WriteScope.All(), model.Schema, options, reconcile: true, ct, _decryptor, _projector, model: model);
                await ComputeTableProcessor.PropagateAsync(guard.Connection, model, plan, r, ct);   // fixed values feed their stored dependents
                await Repo.TouchMaterializedAsync(field.Id, field.MaterializeVersion, ct);
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

    /// <summary>
    /// After midnight UTC (the date Today() and relative-date criteria use on the read path): every Ready field whose value moves with the
    /// date and was last worked out before today is worked out again, inputs first. Its changed rows are propagated, so whatever reads it
    /// follows. MaterializedOn records when the values were last made current.
    /// </summary>
    public async Task<IReadOnlyList<FieldOutcome>> RefreshDailyAsync(ComputeOptions options, CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        var model = await _models.GetAsync(QueryContext.TenantId, Repo, fresh: true, ct);
        var order = model.Graph.TopologicalOrder.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i);
        var outcomes = new List<FieldOutcome>();
        foreach (var (field, plan) in model.Live()
                     .Where(l => l.Plan is ProjectedPlan { Daily: true } && l.Field.MaterializeState == MaterializeStates.Ready
                                 && (l.Field.MaterializedOn ?? DateTime.MinValue) < today)
                     .OrderBy(l => order.TryGetValue(l.Plan.Key, out var i) ? i : int.MaxValue))
        {
            var sw = Stopwatch.StartNew();
            await using var fieldLock = await FieldWorkLock.TryAcquireAsync(ConnectionFactory.CreateAsync, field.Id, ct);
            if (fieldLock is null) continue;   // another replica is refreshing it
            await using var guard = await DatabaseGuard.AcquireAsync(ConnectionFactory.CreateAsync, options, backgroundOnly: true, TimeSpan.FromMinutes(2), _logger, ct);
            if (guard is null) { outcomes.Add(new(field.Id, plan.Fid, plan.TableId, "busy", 0, 0, sw.Elapsed)); break; }
            var sqlType = await SqlColumnType.ReadAsync(guard.Connection, PhysicalNaming.FullTableName(plan.TableId), PhysicalNaming.MaterializedColumnName(plan.Fid), ct: ct);
            if (sqlType is null) continue;
            try
            {
                var r = await MaterializedColumnWriter.RecomputeAsync(guard, plan, sqlType, field.MaterializeVersion, new WriteScope.All(), model.Schema, options, reconcile: false, ct, _decryptor, _projector, model: model);
                await ComputeTableProcessor.PropagateAsync(guard.Connection, model, plan, r, ct);
                await guard.Connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE meta.AppField SET MaterializedOn = SYSUTCDATETIME() WHERE Id = @id AND MaterializeVersion = @v AND MaterializeState = @ready",
                    new { id = field.Id, v = field.MaterializeVersion, ready = MaterializeStates.Ready }, cancellationToken: ct));
                if (_search is not null && r.RowsWritten > 0) await _search.ValuesChangedAsync(field, r.ChangedIds, r.TooManyChanges, ct);
                outcomes.Add(new(field.Id, plan.Fid, plan.TableId, "refreshed", r.RowsWritten, 0, sw.Elapsed));
                _logger?.LogInformation("Compute daily refresh: tenant {TenantId} field {FieldId} (t{TableId}.c_{Fid}): {Rows} value(s) moved with the date, {Seconds:0.0}s.",
                    QueryContext.TenantId, field.Id, plan.TableId, plan.Fid, r.RowsWritten, sw.Elapsed.TotalSeconds);
            }
            catch (DatabaseBusyException) { outcomes.Add(new(field.Id, plan.Fid, plan.TableId, "busy", 0, 0, sw.Elapsed)); break; }
            catch (MaterializeVersionChangedException) { outcomes.Add(new(field.Id, plan.Fid, plan.TableId, "superseded", 0, 0, sw.Elapsed)); }
        }
        if (outcomes.Count > 0) _models.Invalidate(QueryContext.TenantId);
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

    internal static IReadOnlyList<long> InputTables(MaterializePlan plan) => MaterializedColumnWriter.ShapeOf(plan) switch
    {
        SummaryPlan s => new[] { s.ChildTableId, s.ParentTableId }.Distinct().ToList(),
        LookupPlan l => new[] { l.ParentTableId, l.ChildTableId }.Distinct().ToList(),
        _ => [plan.TableId]
    };

    /// <summary>One Id range of a build: <paramref name="Key"/> is the range's first Id as recorded in the checkpoint, <paramref name="Scope"/> where it continues.</summary>
    private sealed record BuildRange(long Key, WriteScope.Range Scope);

    /// <summary>Id ranges per session: smaller ranges balance the sessions better (a session that finishes early takes the next one) and a
    /// stopped build loses at most a range's unreported rows.</summary>
    private const int RangesPerSession = 4;
    private const int MinRowsPerRange = 25_000;

    /// <summary>
    /// The Id ranges a first build is split into, or null when it runs as one range: splitting is on (<see cref="ComputeOptions.SlicesPerField"/>)
    /// and the table has at least <see cref="ComputeOptions.SliceMinRows"/> rows. Every plan kind is split (formulas, read-code fields,
    /// and the set-based lookups / summaries too: each range is one keyset walk over the target table's Id). The row count comes from the
    /// partition statistics and the bounds from MIN / MAX of Id (two index seeks): equal-width ranges, no scan of the table. The first
    /// range starts at 0 and the last is open-ended, so every row is covered, also ones added meanwhile.
    /// </summary>
    private async Task<IReadOnlyList<WriteScope.Range>?> SlicesAsync(MaterializePlan plan, ComputeOptions options, CancellationToken ct)
    {
        if (options.SlicesPerField <= 1) return null;
        var table = PhysicalNaming.FullTableName(MaterializedColumnWriter.ShapeOf(plan).TableId);
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT SUM(row_count) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID(@table) AND index_id IN (0, 1)",
            new { table }, cancellationToken: ct)) ?? 0;
        if (rows < options.SliceMinRows) return null;
        var (minId, maxId) = await conn.QuerySingleAsync<(long? Min, long? Max)>(new CommandDefinition(
            $"SELECT MIN(Id) AS Min, MAX(Id) AS Max FROM {table}", commandTimeout: 120, cancellationToken: ct));
        if (minId is null || maxId is null) return null;
        var count = (int)Math.Min(options.SlicesPerField * RangesPerSession, Math.Max(1, rows / MinRowsPerRange));
        return SliceRanges(minId.Value, maxId.Value, count);
    }

    /// <summary>
    /// <paramref name="count"/> equal-width Id ranges over [minId, maxId]: the first starts at 0, the last is open-ended (long.MaxValue), so
    /// together they cover every Id, also ones added after the bounds were read. Null when fewer than two ranges come out.
    /// </summary>
    internal static IReadOnlyList<WriteScope.Range>? SliceRanges(long minId, long maxId, int count)
    {
        if (count < 2) return null;
        var width = Math.Max(1, (maxId - minId + count) / count);
        var ranges = new List<WriteScope.Range>(count);
        long after = 0;
        for (var i = 1; i < count; i++)
        {
            var upTo = minId - 1 + i * width;
            if (upTo <= after || upTo >= maxId) continue;
            ranges.Add(new WriteScope.Range(after, upTo));
            after = upTo;
        }
        ranges.Add(new WriteScope.Range(after, long.MaxValue));
        return ranges.Count < 2 ? null : ranges;
    }

    /// <summary>
    /// Works through the ranges, side by side on as many background slots as are free (up to <see cref="ComputeOptions.SlicesPerField"/>, at
    /// least one — waited for like a normal build; fewer slots than ranges just means a session takes the next range when it finishes one).
    /// Every range reports its progress to the checkpoint, so a stopped build continues from there. The first failure stops the other
    /// sessions and is rethrown, so the field ends exactly as a one-session build that failed (busy → stays Building, error → Failed).
    /// Null when no slot freed up. No ranges left (everything was done before the stop) = an empty result, no slot needed.
    /// </summary>
    private async Task<(MaterializedColumnWriter.WriteResult Result, int Sessions)?> BuildSlicedAsync(MaterializePlan plan, string sqlType, int version,
        ComputeModel model, IReadOnlyList<BuildRange> work, ComputeOptions options, CancellationToken ct, int keepFree = 0, bool propagate = true)
    {
        if (work.Count == 0) return (new MaterializedColumnWriter.WriteResult(0, 0, [], false), 0);
        var sessions = Math.Max(1, Math.Min(work.Count, options.SlicesPerField));
        var guards = new List<GuardedConnection>();
        try
        {
            var first = await DatabaseGuard.AcquireAsync(ConnectionFactory.CreateAsync, options, backgroundOnly: true, TimeSpan.FromMinutes(2), _logger, ct, keepFree);
            if (first is null) return null;
            guards.Add(first);
            while (guards.Count < sessions
                   && await DatabaseGuard.AcquireAsync(ConnectionFactory.CreateAsync, options, backgroundOnly: true, TimeSpan.Zero, _logger, ct, keepFree) is { } more)
                guards.Add(more);

            var queue = new System.Collections.Concurrent.ConcurrentQueue<BuildRange>(work);
            var results = new System.Collections.Concurrent.ConcurrentBag<MaterializedColumnWriter.WriteResult>();
            Exception? failure = null;
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var repo = Repo;
            await Task.WhenAll(guards.Select(async guard =>
            {
                try
                {
                    while (!stop.IsCancellationRequested && queue.TryDequeue(out var range))
                    {
                        var key = range.Key;
                        results.Add(await MaterializedColumnWriter.RecomputeAsync(guard, plan, sqlType, version, range.Scope, model.Schema, options,
                            reconcile: false, stop.Token, _decryptor, _projector, model: model,
                            onProgress: (doneThrough, written, isDone) => repo.AdvanceCheckpointAsync(plan.FieldId, version, key, doneThrough, written, isDone, stop.Token)));
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref failure, ex, null);
                    stop.Cancel();
                }
            }));
            if (failure is not null)
            {
                ct.ThrowIfCancellationRequested();
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }

            var result = MaterializedColumnWriter.WriteResult.Combine(results.ToList());
            // A field's very first build writes the values the read code already showed (until now every reader worked it out on the fly),
            // so its stored dependents have nothing to catch up with. Propagating it queued a table-wide change for every dependent of
            // every first build: on UAT that sent Ready fields of the 1.1 M-row Sessions table back to a rebuild, over and over.
            if (propagate) await ComputeTableProcessor.PropagateAsync(guards[0].Connection, model, plan, result, ct);
            return (result, guards.Count);
        }
        finally
        {
            foreach (var guard in guards) await guard.DisposeAsync();
        }
    }


    private async Task<bool> HasRowsUpToAsync(IReadOnlyList<long> tables, long watermark, CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM meta.ComputeOutbox WHERE Id <= @watermark AND TableId IN @tables) THEN 1 ELSE 0 END",
            new { watermark, tables }, cancellationToken: ct)) == 1;
    }
}
