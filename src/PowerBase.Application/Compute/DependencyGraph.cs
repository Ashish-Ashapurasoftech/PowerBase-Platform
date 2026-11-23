using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PowerBase.Application.Formulas;
using PowerBase.Application.Reports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Compute;

/// <summary>
/// Which calculated field must be recomputed when which data changes (design 6.3), per tenant, rebuilt when fields change.
/// Nodes are (table, Fid); an edge input -> dependent. A Lookup reads its parent's source field, its own reference and whether the parent
/// row exists; a Summary reads its child's reference, target and criteria fields, whether child rows exist, and parent fields its criteria
/// compare to; a Formula reads what the formula projector says it reads. Walked in topological order; fields in a cycle are reported.
/// </summary>
public sealed class DependencyGraph
{
    private readonly Dictionary<FieldKey, HashSet<FieldKey>> _dependents = new();
    private readonly Dictionary<FieldKey, HashSet<FieldKey>> _inputs = new();
    private readonly HashSet<FieldKey> _computed = new();
    private readonly List<FieldKey> _order;
    private readonly HashSet<FieldKey> _inCycle;
    private readonly HashSet<FieldKey> _unknownInputs = new();

    private static readonly JsonSerializerOptions FilterJson = new() { PropertyNameCaseInsensitive = true };

    /// <param name="formulaReads">The Fids (same table) a formula field reads; null when that can't be told (the field is then treated
    /// as reading everything on its table). When this delegate is null formulas are treated the same way.</param>
    public static DependencyGraph Build(ComputeSchema schema, Func<AppField, IReadOnlyCollection<long>?>? formulaReads = null)
    {
        var graph = new DependencyGraph();
        foreach (var field in schema.Fields)
        {
            if (field.Fid is not int fid || !PhysicalNaming.IsMaterializableTypeCode(field.TypeCode)) continue;
            var key = new FieldKey(field.AppTableId, fid);
            graph._computed.Add(key);
            var inputs = InputsOf(field, schema, formulaReads, out var unknown);
            foreach (var input in inputs) graph.AddEdge(input, key);
            if (unknown) graph._unknownInputs.Add(key);
        }
        return graph.Finish();
    }

    private readonly List<FieldKey> _pendingOrder = new();
    private DependencyGraph()
    {
        _order = _pendingOrder;
        _inCycle = new HashSet<FieldKey>();
    }

    private void AddEdge(FieldKey input, FieldKey dependent)
    {
        if (!_dependents.TryGetValue(input, out var d)) _dependents[input] = d = new HashSet<FieldKey>();
        d.Add(dependent);
        if (!_inputs.TryGetValue(dependent, out var i)) _inputs[dependent] = i = new HashSet<FieldKey>();
        i.Add(input);
    }

    /// <summary>Kahn's algorithm over the calculated fields only: whatever cannot be ordered is in (or behind) a cycle.</summary>
    private DependencyGraph Finish()
    {
        var indegree = _computed.ToDictionary(k => k, k => _inputs.TryGetValue(k, out var ins) ? ins.Count(_computed.Contains) : 0);
        var ready = new SortedSet<FieldKey>(indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key), KeyOrder.Instance);
        while (ready.Count > 0)
        {
            var next = ready.Min;
            ready.Remove(next);
            _pendingOrder.Add(next);
            if (!_dependents.TryGetValue(next, out var deps)) continue;
            foreach (var dep in deps)
                if (_computed.Contains(dep) && --indegree[dep] == 0) ready.Add(dep);
        }
        foreach (var key in _computed)
            if (!_pendingOrder.Contains(key)) _inCycle.Add(key);
        return this;
    }

    /// <summary>Calculated fields in an order where every field comes after the fields it reads.</summary>
    public IReadOnlyList<FieldKey> TopologicalOrder => _order;

    /// <summary>Fields in a dependency cycle (or depending on one): no correct order exists, so they are never stored.</summary>
    public IReadOnlySet<FieldKey> InCycle => _inCycle;

    /// <summary>Formula fields whose reads could not be listed.</summary>
    public IReadOnlySet<FieldKey> UnknownInputs => _unknownInputs;

    /// <summary>The calculated fields that read <paramref name="field"/> directly.</summary>
    public IReadOnlyCollection<FieldKey> DependentsOf(FieldKey field) =>
        _dependents.TryGetValue(field, out var set) ? set.Where(_computed.Contains).ToList() : [];

    public IReadOnlySet<FieldKey> InputsOf(FieldKey field) =>
        _inputs.TryGetValue(field, out var set) ? set : new HashSet<FieldKey>();

    /// <summary>
    /// The calculated fields a change makes stale, directly or through other calculated fields, in topological order (cycle members last).
    /// <paramref name="changedFids"/> null = every field of the table changed; <paramref name="rowsChanged"/> = rows were created, deleted
    /// or restored. Formula fields whose reads are unknown are affected by any change on their table.
    /// </summary>
    public IReadOnlyList<FieldKey> AffectedBy(long tableId, IReadOnlyCollection<int>? changedFids, bool rowsChanged)
    {
        var start = new HashSet<FieldKey>();
        foreach (var (input, dependents) in _dependents)
        {
            if (input.TableId != tableId) continue;
            var hit = input.Fid == FieldKey.RowsFid ? rowsChanged || changedFids is null : changedFids is null || changedFids.Contains(input.Fid);
            if (hit) start.UnionWith(dependents);
        }
        foreach (var key in _unknownInputs)
            if (key.TableId == tableId) start.Add(key);

        var affected = new HashSet<FieldKey>();
        var queue = new Queue<FieldKey>(start);
        while (queue.Count > 0)
        {
            var key = queue.Dequeue();
            if (!_computed.Contains(key) || !affected.Add(key)) continue;
            if (_dependents.TryGetValue(key, out var next)) foreach (var n in next) queue.Enqueue(n);
        }
        return _order.Where(affected.Contains).Concat(affected.Where(_inCycle.Contains).OrderBy(k => k, KeyOrder.Instance)).ToList();
    }

    /// <summary>
    /// The data one calculated field reads, from its own settings (no schema needed): what a definition change of any of these makes stale.
    /// Null for a formula, whose reads only the formula projector can list (treated as "reads anything on its table").
    /// </summary>
    public static IReadOnlyCollection<FieldKey>? InputKeysOf(AppField field)
    {
        var keys = InputsOf(field, null!, null, out var unknown).ToList();
        return unknown ? null : keys;
    }

    private static IEnumerable<FieldKey> InputsOf(AppField field, ComputeSchema schema, Func<AppField, IReadOnlyCollection<long>?>? formulaReads, out bool unknown)
    {
        unknown = false;
        var table = field.AppTableId;
        var inputs = new List<FieldKey>();
        switch (field.TypeCode)
        {
            case "Lookup":
                if (FormulaTypeMap.ParseLookupSettings(field.Settings) is { SourceTableId: long parent, ReferenceFid: int lRef, SourceFid: int source })
                {
                    inputs.Add(new FieldKey(table, lRef));
                    inputs.Add(new FieldKey(parent, source));
                    inputs.Add(FieldKey.Rows(parent));
                    inputs.Add(FieldKey.Rows(table));      // a new child row gets its lookup value
                }
                break;
            case "Summary":
                if (FormulaTypeMap.ParseSummarySettings(field.Settings) is { ChildTableId: long child, ReferenceFid: int sRef } s)
                {
                    inputs.Add(new FieldKey(child, sRef));
                    inputs.Add(FieldKey.Rows(child));
                    inputs.Add(FieldKey.Rows(table));      // a new parent row gets its 0 / false / NULL
                    if (s.TargetFid is int target) inputs.Add(new FieldKey(child, target));
                    if (s.SortFid is int sort) inputs.Add(new FieldKey(child, sort));
                    foreach (var (fid, onParent) in CriteriaFields(s.FilterTree))
                        inputs.Add(new FieldKey(onParent ? table : child, fid));
                }
                break;
            default:   // Formula and Formula_*
                var reads = formulaReads?.Invoke(field);
                if (reads is null) unknown = true;
                else inputs.AddRange(reads.Where(f => f != field.Fid).Select(f => new FieldKey(table, (int)f)));
                break;
        }
        return inputs;
    }

    /// <summary>Fids the criteria read: the condition's own field (child), and the compared field (child for "field", parent for "parentField").</summary>
    internal static IEnumerable<(int Fid, bool OnParent)> CriteriaFields(string? filterJson)
    {
        if (string.IsNullOrWhiteSpace(filterJson)) yield break;
        FilterGroup? group;
        try { group = JsonSerializer.Deserialize<FilterGroup>(filterJson, FilterJson); }
        catch (JsonException) { yield break; }
        var stack = new Stack<FilterGroup>();
        if (group is not null) stack.Push(group);
        while (stack.Count > 0)
        {
            foreach (var node in stack.Pop().Nodes)
            {
                if (node.Group is { } g) stack.Push(g);
                if (node.Condition is not { } c) continue;
                yield return ((int)c.FieldId, false);
                if (c.ValueFieldId is long other)
                    yield return ((int)other, string.Equals(c.ValueMode, "parentField", StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    private sealed class KeyOrder : IComparer<FieldKey>
    {
        public static readonly KeyOrder Instance = new();
        public int Compare(FieldKey a, FieldKey b) => a.TableId != b.TableId ? a.TableId.CompareTo(b.TableId) : a.Fid.CompareTo(b.Fid);
    }
}
