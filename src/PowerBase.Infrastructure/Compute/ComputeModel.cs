using System.Collections.Concurrent;
using PowerBase.Application.Compute;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.Infrastructure.Compute;

/// <summary>A tenant's fields with their dependency graph and the rule decision for every calculated field.</summary>
public sealed class ComputeModel
{
    public ComputeModel(ComputeSchema schema)
    {
        Schema = schema;
        // A formula's reads come from compiling it (the same engine the API uses); unknown reads make it depend on its whole table.
        Graph = DependencyGraph.Build(schema, f =>
            FormulaAnalysis.Analyze(f, schema.FieldsOf(f.AppTableId)) is { UsesDynamicFieldReference: false, HasErrors: false, UsesVariables: false } info
                ? info.Reads : null);

        // Topological order: a field's calculated inputs are decided before it, so a chain is allowed only on top of allowed inputs.
        var decisions = new Dictionary<FieldKey, (AppField, MaterializeDecision)>();
        MaterializeDecision? DecisionOf(FieldKey key) => decisions.TryGetValue(key, out var d) ? d.Item2 : null;
        var fieldsByKey = schema.Fields.Where(f => f.Fid.HasValue && PhysicalNaming.IsMaterializableTypeCode(f.TypeCode))
            .GroupBy(f => new FieldKey(f.AppTableId, f.Fid!.Value)).ToDictionary(g => g.Key, g => g.First());
        foreach (var key in Graph.TopologicalOrder.Concat(fieldsByKey.Keys.Where(k => !Graph.TopologicalOrder.Contains(k))))
            if (fieldsByKey.TryGetValue(key, out var field))
                decisions[key] = (field, MaterializeRules.Evaluate(field, schema, Graph.InCycle, DecisionOf));
        Decisions = decisions;
    }

    public ComputeSchema Schema { get; }
    public DependencyGraph Graph { get; }
    public IReadOnlyDictionary<FieldKey, (AppField Field, MaterializeDecision Decision)> Decisions { get; }

    /// <summary>Switched-on fields whose column exists and is kept up to date by live changes: Building or Ready, and allowed.</summary>
    public IEnumerable<(AppField Field, MaterializePlan Plan)> Live() =>
        Decisions.Values
            .Where(d => d.Field.MaterializeMode == MaterializeModes.On
                && d.Field.MaterializeState is MaterializeStates.Building or MaterializeStates.Ready
                && d.Decision is { Outcome: MaterializeOutcome.Allowed, Plan: not null })
            .Select(d => (d.Field, d.Decision.Plan!));
}

/// <summary>
/// Per-tenant <see cref="ComputeModel"/> from the primary. Reused only while the fingerprint of the switched-on fields is unchanged (checked
/// on every use, one tiny query), so a field moving to Building or getting a new version is seen at once by every replica; reloaded at
/// least every few minutes for everything else (new fields, relationships).
/// </summary>
public sealed class ComputeModelCache
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<long, (ComputeModel Model, DateTime LoadedUtc, long Fingerprint)> _models = new();

    public async Task<ComputeModel> GetAsync(long tenantId, ComputeSchemaRepository repository, bool fresh, CancellationToken ct)
    {
        var fingerprint = await repository.FingerprintAsync(ct);
        if (!fresh && _models.TryGetValue(tenantId, out var hit) && hit.Fingerprint == fingerprint && DateTime.UtcNow - hit.LoadedUtc < MaxAge)
            return hit.Model;
        var model = new ComputeModel(await repository.LoadAsync(ct));
        _models[tenantId] = (model, DateTime.UtcNow, fingerprint);
        return model;
    }

    public void Invalidate(long tenantId) => _models.TryRemove(tenantId, out _);
}
