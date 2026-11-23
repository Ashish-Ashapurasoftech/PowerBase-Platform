using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Compute;
using PowerBase.Domain.Constants;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Repositories;

namespace PowerBase.Infrastructure.Compute;

/// <summary>
/// Live changes of one table (one Service Bus session = one tenant + table): reads the waiting ComputeOutbox rows, works out which stored
/// columns they make stale through the dependency graph, recomputes exactly those rows (old AND new parent on a reference change), and
/// deletes the rows only after the work succeeded. Ten edits to one parent become one recompute (coalescing). Uses the PRIMARY only: a
/// live event must never read a replica that is behind, or the consumed row would never fix the value it stored. Runs with no user.
/// </summary>
public sealed class ComputeTableProcessor : TenantRepositoryBase
{
    private const int MaxRounds = 20;
    private readonly ComputeModelCache _models;
    private readonly ComputeSearchNotifier? _search;
    private readonly ILogger<ComputeTableProcessor>? _logger;

    public ComputeTableProcessor(ITenantConnectionFactory connectionFactory, IQueryContext queryContext, ComputeModelCache models,
        ILogger<ComputeTableProcessor>? logger = null, ComputeSearchNotifier? search = null)
        : base(connectionFactory, queryContext)
    {
        _search = search;
        _models = models;
        _logger = logger;
    }

    internal sealed record OutboxRow(long Id, long? RecordId, Guid? RecordPublicId, byte Reason, string? ChangedFids, string? OldRefs);

    /// <summary>What a set of outbox rows of one table says changed.</summary>
    internal sealed record ChangeSet(bool TableWide, IReadOnlyCollection<int>? ChangedFids, bool RowsChanged,
        IReadOnlyCollection<long> RecordIds, IReadOnlyDictionary<int, IReadOnlySet<long>> OldParents);

    /// <param name="TablesToNotify">Other tables that got follow-up rows (chained stored fields): the caller tells the queue.</param>
    public sealed record DrainResult(int RowsProcessed, int FieldsRecomputed, long ValuesWritten, IReadOnlyCollection<long> TablesToNotify);

    /// <summary>Processes every waiting row of <paramref name="tableId"/> (bounded rounds). Throws <see cref="DatabaseBusyException"/>
    /// when no worker slot or no capacity is free: the caller retries later and the rows stay.</summary>
    public async Task<DrainResult> DrainAsync(long tableId, ComputeOptions options, CancellationToken ct)
    {
        var schemaRepo = new ComputeSchemaRepository(ConnectionFactory, QueryContext);
        int processed = 0, fields = 0;
        long values = 0;
        var notify = new HashSet<long>();

        await using var guard = await DatabaseGuard.AcquireAsync(ConnectionFactory.CreateAsync, options, backgroundOnly: false,
            TimeSpan.FromSeconds(30), _logger, ct) ?? throw new DatabaseBusyException("No compute slot is free in this database.");

        for (var round = 0; round < MaxRounds; round++)
        {
            var rows = (await guard.Connection.QueryAsync<OutboxRow>(new CommandDefinition("""
                SELECT TOP (@take) Id, RecordId, RecordPublicId, Reason, ChangedFids, OldRefs
                FROM meta.ComputeOutbox WHERE TableId = @tableId ORDER BY Id
                """, new { take = options.DrainBatchSize, tableId }, cancellationToken: ct))).AsList();
            if (rows.Count == 0) break;

            var model = await _models.GetAsync(QueryContext.TenantId, schemaRepo, fresh: false, ct);
            var change = await ReadChangeAsync(guard.Connection, tableId, rows, ct);
            var targets = Targets(model, tableId, change);

            foreach (var (field, plan, scope) in targets)
            {
                var column = PhysicalNaming.MaterializedColumnName(plan.Fid);
                var sqlType = await SqlColumnType.ReadAsync(guard.Connection, PhysicalNaming.FullTableName(plan.TableId), column, ct: ct);
                if (sqlType is null) continue;   // column not created yet: the build that creates it computes everything anyway
                try
                {
                    var concrete = await ComputeScopes.ResolveAsync(guard.Connection, plan, scope, ct);
                    var result = await MaterializedColumnWriter.RecomputeAsync(guard, plan, sqlType, field.MaterializeVersion, concrete,
                        model.Schema, options, reconcile: false, ct);
                    fields++;
                    values += result.RowsWritten;
                    foreach (var t in await PropagateAsync(guard.Connection, model, plan, result, ct)) notify.Add(t);
                    if (_search is not null) await _search.ValuesChangedAsync(field, result.ChangedIds, result.TooManyChanges, ct);
                }
                catch (MaterializeVersionChangedException)
                {
                    // The field's definition changed: its rebuild recomputes every row for the new version. Nothing to do here.
                    _models.Invalidate(QueryContext.TenantId);
                }
                catch (SqlException ex) when (ex.Number is 2601 or 2627)
                {
                    // A unique formula value collided. The save path refuses duplicates in its own transaction, so this is a write that
                    // bypassed it (refused imports, a script): reported, the rest of the work goes on, the nightly check reports it again.
                    _logger?.LogError("Compute: a unique stored value of field {FieldId} (t{TableId}.c_{Fid}) collides; those rows keep their old value. {Message}",
                        field.Id, plan.TableId, plan.Fid, ex.Message);
                }
            }

            await guard.Connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM meta.ComputeOutbox WHERE Id IN (SELECT k FROM OPENJSON(@ids) WITH (k BIGINT '$'))",
                new { ids = JsonSerializer.Serialize(rows.Select(r => r.Id)) }, cancellationToken: ct));
            processed += rows.Count;
        }

        notify.Remove(tableId);   // this table's own follow-up rows are drained above
        return new DrainResult(processed, fields, values, notify);
    }

    /// <summary>
    /// A stored field that changed is an input of its stored dependents (a chain): queues "these rows of my table changed, field F" so the
    /// dependents recompute exactly those rows — through the same outbox, so it is durable and coalesced like any save. Returns the tables
    /// that got rows (to tell the queue). Nothing when nothing changed or nothing stored reads the field.
    /// </summary>
    internal static async Task<IReadOnlyCollection<long>> PropagateAsync(SqlConnection connection, ComputeModel model, MaterializePlan plan,
        MaterializedColumnWriter.WriteResult result, CancellationToken ct)
    {
        if (result.RowsWritten == 0) return [];
        var live = model.Live().Select(l => l.Plan.Key).ToHashSet();
        if (!model.Graph.DependentsOf(plan.Key).Any(live.Contains)) return [];

        IReadOnlyList<ComputeChange> changes = result.TooManyChanges
            ? [new ComputeChange(plan.TableId, ComputeChangeReason.TableWide, ChangedFids: [plan.Fid])]
            : ComputeOutboxPlanner.CollapseLargeBatch(result.ChangedIds
                .Select(id => new ComputeChange(plan.TableId, ComputeChangeReason.Update, RecordId: id, ChangedFids: [plan.Fid])).ToList());
        foreach (var chunk in changes.Chunk(ComputeOutboxRepository.InsertChunk))
        {
            var (sql, parameters) = ComputeOutboxRepository.BuildInsert(chunk);
            await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));
        }
        return [plan.TableId];
    }

    /// <summary>Folds the rows into one change: the changed fids (null = unknown/all), the record Ids, the old parents per reference field.</summary>
    internal async Task<ChangeSet> ReadChangeAsync(SqlConnection connection, long tableId, IReadOnlyList<OutboxRow> rows, CancellationToken ct)
    {
        var tableWide = rows.Any(r => r.RecordId is null && r.RecordPublicId is null);
        var allFids = false;
        var fids = new HashSet<int>();
        var oldParents = new Dictionary<int, HashSet<long>>();
        var rowsChanged = tableWide;
        foreach (var r in rows)
        {
            var reason = (ComputeChangeReason)r.Reason;
            if (reason is ComputeChangeReason.Create or ComputeChangeReason.Delete or ComputeChangeReason.TableWide) rowsChanged = true;
            var rowFids = ComputeOutboxJson.ParseFids(r.ChangedFids);
            if (rowFids.Count == 0 && reason is not (ComputeChangeReason.Create or ComputeChangeReason.Delete)) allFids = true;
            fids.UnionWith(rowFids);
            foreach (var (fid, parent) in ComputeOutboxJson.ParseOldRefs(r.OldRefs))
            {
                if (parent is not long p) continue;
                if (!oldParents.TryGetValue(fid, out var set)) oldParents[fid] = set = new HashSet<long>();
                set.Add(p);
            }
        }

        var ids = rows.Where(r => r.RecordId.HasValue).Select(r => r.RecordId!.Value).ToHashSet();
        var publicIds = rows.Where(r => r.RecordId is null && r.RecordPublicId.HasValue).Select(r => r.RecordPublicId!.Value).Distinct().ToList();
        foreach (var chunk in publicIds.Chunk(1000))
            ids.UnionWith(await connection.QueryAsync<long>(new CommandDefinition(
                $"DECLARE @g TABLE (k UNIQUEIDENTIFIER NOT NULL PRIMARY KEY); INSERT INTO @g (k) SELECT DISTINCT CAST(k AS UNIQUEIDENTIFIER) FROM OPENJSON(@ids) WITH (k NVARCHAR(36) '$'); SELECT t.Id FROM {PhysicalNaming.FullTableName(tableId)} t JOIN @g g ON g.k = t.PublicId;",
                new { ids = JsonSerializer.Serialize(chunk) }, cancellationToken: ct)));

        return new ChangeSet(tableWide, tableWide || allFids ? null : fids, rowsChanged, ids,
            oldParents.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<long>)kv.Value));
    }

    /// <summary>
    /// The stored fields to recompute for a change of <paramref name="tableId"/>, and which of their rows. A Summary whose CHILD changed
    /// recomputes the children's current parents plus their old parents; a Lookup whose PARENT changed recomputes that parent's children;
    /// a field whose own table changed recomputes those rows. A table-wide change recomputes the whole field.
    /// </summary>
    internal static IReadOnlyList<(Domain.Entities.AppField Field, MaterializePlan Plan, WriteScope Scope)> Targets(ComputeModel model, long tableId, ChangeSet change)
    {
        var affected = model.Graph.AffectedBy(tableId, change.ChangedFids, change.RowsChanged).ToHashSet();
        var result = new List<(Domain.Entities.AppField, MaterializePlan, WriteScope)>();
        foreach (var (field, plan) in model.Live())
        {
            if (!affected.Contains(plan.Key)) continue;
            if (change.TableWide)
            {
                result.Add((field, plan, new WriteScope.All()));
                continue;
            }
            switch (plan)
            {
                case SummaryPlan s:
                    if (s.ChildTableId == tableId)
                        result.Add((field, plan, new ParentsPending(s.ReferenceFid, change.RecordIds,
                            change.OldParents.TryGetValue(s.ReferenceFid, out var old) ? old : new HashSet<long>())));
                    if (s.ParentTableId == tableId && change.RecordIds.Count > 0)
                        result.Add((field, plan, new WriteScope.Rows(change.RecordIds)));
                    break;
                case LookupPlan l:
                    if (l.ParentTableId == tableId && change.RecordIds.Count > 0)
                        result.Add((field, plan, new WriteScope.ChildrenOf(change.RecordIds)));
                    if (l.ChildTableId == tableId && change.RecordIds.Count > 0)
                        result.Add((field, plan, new WriteScope.Rows(change.RecordIds)));
                    break;
                case FormulaPlan f:
                    if (f.TableId == tableId && change.RecordIds.Count > 0)
                        result.Add((field, plan, new WriteScope.Rows(change.RecordIds)));
                    break;
            }
        }
        return result;
    }
}

/// <summary>
/// A Summary's parents to recompute after children changed: the children's CURRENT parents (read when the work runs, from the soft-deleted
/// rows too) plus the OLD parents the outbox captured. Resolved into <see cref="WriteScope.Rows"/> by <see cref="ComputeScopes"/>.
/// </summary>
public sealed record ParentsPending(int ReferenceFid, IReadOnlyCollection<long> ChildIds, IReadOnlySet<long> OldParents) : WriteScope;

public static class ComputeScopes
{
    /// <summary>Turns <see cref="ParentsPending"/> into the concrete parent Ids; any other scope is returned as it is.</summary>
    public static async Task<WriteScope> ResolveAsync(SqlConnection connection, MaterializePlan plan, WriteScope scope, CancellationToken ct)
    {
        if (scope is not ParentsPending pending || plan is not SummaryPlan s) return scope;
        var parents = new HashSet<long>(pending.OldParents);
        var refCol = PhysicalNaming.ColumnName(pending.ReferenceFid);
        foreach (var chunk in pending.ChildIds.Chunk(2000))
            parents.UnionWith(await connection.QueryAsync<long>(new CommandDefinition(
                $"{MaterializedColumnWriter.KeysSql}SELECT DISTINCT CAST(c.{refCol} AS BIGINT) FROM {PhysicalNaming.FullTableName(s.ChildTableId)} c JOIN @k k ON k.k = c.Id WHERE c.{refCol} IS NOT NULL;",
                new { ids = JsonSerializer.Serialize(chunk) }, cancellationToken: ct)));
        return new WriteScope.Rows(parents);
    }
}
