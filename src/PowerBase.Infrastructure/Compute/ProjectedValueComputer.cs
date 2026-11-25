using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Compute;
using PowerBase.Application.Formulas;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.Infrastructure.Compute;

/// <summary>
/// Works out the values of a <see cref="ProjectedPlan"/> field for a batch of rows with the API's own read code — RelationalProjector for
/// Lookups and Summaries, FormulaProjector for formulas, exactly what a report runs — so the stored value is the value shown, however the
/// field is built (chains of calculated fields, criteria on calculated or parent fields, relative dates, Combined Text, URLRoot()…).
/// The rows are read decrypted by the record repository, as for a report. The field itself is never read from its own old column
/// (<see cref="StoredReadExclusion"/>); the calculated fields it reads come from their stored columns when Ready, else are worked out.
/// One instance per tenant scope.
/// </summary>
public sealed class ProjectedValueComputer
{
    private readonly IServiceProvider _services;
    private readonly Dictionary<long, (AppTable Table, IReadOnlyList<AppField> Fields)> _tables = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    /// <summary>Fields already reported as "dependencies unknown" (one warning per field per process).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> LoggedAllFields = new();

    public ProjectedValueComputer(IServiceProvider services) => _services = services;

    private async Task<(AppTable Table, IReadOnlyList<AppField> Fields)> TableAsync(long tableId, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_tables.TryGetValue(tableId, out var cached)) return cached;
            var table = await _services.GetRequiredService<IAppTableRepository>().GetByIdAsync(tableId, ct);
            var fields = await _services.GetRequiredService<IAppFieldRepository>().ListByTableAsync(tableId, ct);
            return _tables[tableId] = (table, fields);
        }
        finally { _lock.Release(); }
    }

    /// <summary>(row Id, value) for every id; a row that is gone (deleted) gets NULL, as nothing shows it.</summary>
    public async Task<IReadOnlyList<(long Id, object? Value)>> ComputeAsync(ProjectedPlan plan, long[] ids, CancellationToken ct)
    {
        var (table, fields) = await TableAsync(plan.TableId, ct);
        var field = fields.FirstOrDefault(f => f.Id == plan.FieldId)
            ?? throw new InvalidOperationException($"Field {plan.FieldId} is no longer on table {plan.TableId}.");

        // Each projector run gets its own scope: they hold per-request caches (decrypted child rows, users) that must not grow across batches.
        using var scope = _services.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<IQueryContext>().SetTenantId(_services.GetRequiredService<IQueryContext>().TenantId);

        var physical = fields.Where(f => f.Fid.HasValue && !PhysicalNaming.IsComputedTypeCode(f.TypeCode)).ToList();
        var byId = await sp.GetRequiredService<IRecordRepository>().GetRowsByIdsAsync(table, physical, ids, ct);
        var present = ids.Where(byId.ContainsKey).ToArray();
        var rows = present.Select(id => byId[id]).ToList();

        var values = new Dictionary<long, object?>();
        if (rows.Count > 0)
        {
            using var _ = StoredReadExclusion.Exclude(field.Id);
            var relational = sp.GetRequiredService<IRelationalProjector>();
            if (field.TypeCode is "Lookup" or "Summary")
            {
                var maps = await relational.ProjectAsync(table, [field], rows, ct);
                for (var i = 0; i < rows.Count; i++) values[present[i]] = maps[i].TryGetValue(field.Fid!.Value, out var v) ? v : null;
            }
            else
            {
                // The formula and what it reads (other formulas, lookups, summaries), seeded the way a report seeds them.
                var formulas = sp.GetRequiredService<IFormulaProjector>();
                var reads = formulas.GetFormulaDependencies(fields, [field.Fid!.Value]);
                if (reads is null && LoggedAllFields.TryAdd(field.Id, 0))
                    _services.GetService<Microsoft.Extensions.Logging.ILogger<ProjectedValueComputer>>()?.LogWarning(
                        "Compute: formula field {FieldId} (t{TableId}.c_{Fid}): its dependencies could not be worked out, so every calculated field of the table is computed for each batch (slow).",
                        field.Id, table.Id, field.Fid);
                var needed = reads is null
                    ? fields
                    : fields.Where(f => f.Fid is int fid && (fid == field.Fid || reads.Contains(fid) || !PhysicalNaming.IsComputedTypeCode(f.TypeCode))).ToList();
                var seed = await relational.ProjectAsync(table, needed, rows, ct);
                var maps = formulas.Project(needed, rows, seed, table);
                for (var i = 0; i < rows.Count; i++) values[present[i]] = maps[i].TryGetValue(field.Fid!.Value, out var v) ? v : null;
            }
        }
        return ids.Select(id => (id, values.TryGetValue(id, out var v) ? v : null)).ToList();
    }
}
