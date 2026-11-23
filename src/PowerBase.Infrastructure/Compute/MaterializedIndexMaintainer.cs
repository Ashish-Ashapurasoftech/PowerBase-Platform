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
/// Keeps the sort / filter indexes of stored columns (IX_t_X_c_N) in line with what the tenant's saved reports and role rules use
/// (<see cref="StoredIndexPlanner"/>): an index is built once a used field is Ready, and dropped when no report or rule uses the field any
/// more, so the worker's writes only pay for indexes that reads need. Index builds take the database guard's background slot.
/// </summary>
public sealed class MaterializedIndexMaintainer : TenantRepositoryBase
{
    private readonly ISchemaEngineService _schemaEngine;
    private readonly ComputeModelCache _models;
    private readonly ILogger<MaterializedIndexMaintainer>? _logger;

    public MaterializedIndexMaintainer(ITenantConnectionFactory connectionFactory, IQueryContext queryContext, ISchemaEngineService schemaEngine,
        ComputeModelCache models, ILogger<MaterializedIndexMaintainer>? logger = null)
        : base(connectionFactory, queryContext)
    {
        _schemaEngine = schemaEngine;
        _models = models;
        _logger = logger;
    }

    public sealed record IndexOutcome(long FieldId, long TableId, int Fid, string Outcome, TimeSpan Took);

    private const string ReportsSql = """
        SELECT AppTableId AS TableId, Definition AS Json FROM meta.Report WHERE IsDeleted = 0 AND AppTableId IN @tables;
        SELECT AppTableId AS TableId, FilterJson AS Json FROM meta.AppRoleRecordFilter WHERE AppTableId IN @tables;
        """;

    private sealed record JsonRow(long TableId, string? Json);

    /// <summary>One pass for the current tenant: creates the missing indexes, drops the unused ones.</summary>
    public async Task<IReadOnlyList<IndexOutcome>> SyncAsync(ComputeOptions options, CancellationToken ct)
    {
        var model = await _models.GetAsync(QueryContext.TenantId, new ComputeSchemaRepository(ConnectionFactory, QueryContext), fresh: false, ct);
        var switchedOn = model.Decisions.Values.Where(d => d.Field.MaterializeMode == MaterializeModes.On && d.Field.Fid.HasValue).ToList();
        var outcomes = new List<IndexOutcome>();
        if (switchedOn.Count == 0) return outcomes;

        var tables = switchedOn.Select(d => d.Field.AppTableId).Distinct().ToList();
        List<JsonRow> reports, rules;
        await using (var conn = await OpenConnectionAsync(ct))
        await using (var grid = await conn.QueryMultipleAsync(new CommandDefinition(ReportsSql, new { tables }, cancellationToken: ct)))
        {
            reports = (await grid.ReadAsync<JsonRow>()).AsList();
            rules = (await grid.ReadAsync<JsonRow>()).AsList();
        }

        var fieldsByTable = tables.ToDictionary(t => t, t => (IReadOnlyList<AppField>)model.Schema.FieldsOf(t).ToList());
        var used = StoredIndexPlanner.FieldsToIndex(
            switchedOn.Select(d => d.Field.Id).ToHashSet(), fieldsByTable,
            reports.Select(r => (r.TableId, r.Json)), rules.Select(r => (r.TableId, r.Json)));

        foreach (var table in tables)
        {
            ct.ThrowIfCancellationRequested();
            var existing = await _schemaEngine.ListMaterializedIndexesAsync(table, ct);
            foreach (var (field, decision) in switchedOn.Where(d => d.Field.AppTableId == table).OrderBy(d => d.Field.Id))
            {
                var fid = field.Fid!.Value;
                // A unique formula already has UX_t_X_c_N on the same column, which serves its sorts and filters.
                var isUnique = decision.Plan is FormulaPlan { IsUnique: true };
                var keep = used.Contains(field.Id) && !isUnique;
                var has = existing.Contains(fid);

                if (has && !keep)
                {
                    await _schemaEngine.DropMaterializedIndexAsync(table, fid, ct);
                    outcomes.Add(new(field.Id, table, fid, "dropped", TimeSpan.Zero));
                    _logger?.LogInformation("Compute index: tenant {TenantId} dropped IX_t{TableId}_c_{Fid} (no report or rule uses field {FieldId}).",
                        QueryContext.TenantId, table, fid, field.Id);
                    continue;
                }
                if (has || !keep || field.MaterializeState != MaterializeStates.Ready) continue;

                var outcome = await CreateAsync(field, table, fid, options, ct);
                outcomes.Add(outcome);
                if (outcome.Outcome == "busy") return outcomes;   // the database is busy: the next round continues
            }
        }
        return outcomes;
    }

    private async Task<IndexOutcome> CreateAsync(AppField field, long table, int fid, ComputeOptions options, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await using var guard = await DatabaseGuard.AcquireAsync(ConnectionFactory.CreateAsync, options, backgroundOnly: true, TimeSpan.FromMinutes(2), _logger, ct);
        if (guard is null) return new(field.Id, table, fid, "busy", sw.Elapsed);

        var sqlType = await SqlColumnType.ReadAsync(guard.Connection, PhysicalNaming.FullTableName(table), PhysicalNaming.MaterializedColumnName(fid), ct: ct);
        if (sqlType is null) return new(field.Id, table, fid, "missing-column", sw.Elapsed);
        if (!StoredIndexPlanner.IsWorthIndexing(sqlType)) return new(field.Id, table, fid, "not-worth-it", sw.Elapsed);

        var created = await _schemaEngine.EnsureMaterializedIndexAsync(table, fid, ct);
        _logger?.LogInformation("Compute index: tenant {TenantId} {Action} IX_t{TableId}_c_{Fid} for field {FieldId} in {Seconds:0.0}s.",
            QueryContext.TenantId, created ? "built" : "found", table, fid, field.Id, sw.Elapsed.TotalSeconds);
        return new(field.Id, table, fid, created ? "built" : "exists", sw.Elapsed);
    }
}
