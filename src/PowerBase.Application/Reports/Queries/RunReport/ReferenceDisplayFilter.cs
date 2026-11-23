using PowerBase.Application.Formulas;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Enums;

namespace PowerBase.Application.Reports.Queries.RunReport;

/// <summary>
/// A report filters a Reference by the value its grid shows, not by the Record ID# its column stores:
/// <list type="bullet">
/// <item>a Reference whose parent shows a key field (an alternate key / Set Key, e.g. "Program ID") is compared as that key —
/// through a stand-in lookup of it (<see cref="ReferenceKeys.StandIn"/>), which the SQL layer reads from the parent row;</item>
/// <item>a Reference converted from an existing field keeps the values that matched no parent in a hidden "(original)" field
/// (<c>ReferenceSettings.OriginalFid</c>), which the grid shows where the reference is blank
/// (<see cref="ReferenceOriginalFallback"/>) — so an unlinked row is compared as that original text.</item>
/// </list>
/// A condition on such a Reference becomes: (linked AND the key matches) OR (not linked AND the original text matches).
/// "is not blank" then finds an unlinked "XYZ", and "contains ABC" finds a client linked to the program keyed "ABC".
/// A Reference that shows its Record ID# and kept no original text is left exactly as it was.
/// </summary>
public static class ReferenceDisplayFilter
{
    /// <summary>The rewritten tree and the stand-in key lookups it reads (to add to the fields the query knows), or null when
    /// nothing in <paramref name="tree"/> filters such a Reference.</summary>
    public static (FilterGroup Tree, IReadOnlyList<AppField> StandIns)? Rewrite(
        FilterGroup? tree, IReadOnlyList<AppField> fields, IReadOnlyDictionary<long, AppField>? referenceKeys)
    {
        if (tree is null) return null;
        var byFid = fields.Where(f => f.Fid.HasValue).GroupBy(f => (long)f.Fid!.Value).ToDictionary(g => g.Key, g => g.First());

        // The original-text field of each Reference the tree filters, when it is still there.
        var originals = new Dictionary<long, long>();
        foreach (var fid in SummaryEncryptionGuard.ConditionFieldIds(tree).Distinct())
        {
            if (!byFid.TryGetValue(fid, out var f) || f.TypeCode != nameof(FieldTypeCode.Reference)) continue;
            if (FormulaTypeMap.ParseReferenceSettings(f.Settings)?.OriginalFid is int original && byFid.ContainsKey(original))
                originals[fid] = original;
        }

        // Stand-in lookups of the keys (only for the References in the tree that show a key field).
        var keys = ReferenceKeys.StandIn(tree, null, byFid, referenceKeys);
        var moved = keys?.Moved ?? new Dictionary<long, long>();
        if (moved.Count == 0 && originals.Count == 0) return null;

        var rewritten = RewriteGroup(tree, moved, originals);
        return (rewritten, keys?.Lookups ?? []);
    }

    private static FilterGroup RewriteGroup(FilterGroup group, IReadOnlyDictionary<long, long> moved, IReadOnlyDictionary<long, long> originals)
    {
        var copy = new FilterGroup { Logic = group.Logic };
        foreach (var node in group.Nodes)
        {
            if (node.Group is { } sub) copy.Nodes.Add(new FilterNode { Group = RewriteGroup(sub, moved, originals) });
            else if (node.Condition is { } c) copy.Nodes.Add(RewriteCondition(c, moved, originals));
        }
        return copy;
    }

    private static FilterNode RewriteCondition(FilterCondition c, IReadOnlyDictionary<long, long> moved, IReadOnlyDictionary<long, long> originals)
    {
        // "the value in another field" comparisons keep comparing the stored values, as before.
        var literal = c.ValueMode is null || string.Equals(c.ValueMode, "literal", StringComparison.OrdinalIgnoreCase);
        var hasKey = moved.TryGetValue(c.FieldId, out var standIn);
        var hasOriginal = originals.TryGetValue(c.FieldId, out var originalFid);
        if (!literal || (!hasKey && !hasOriginal)) return new FilterNode { Condition = Clone(c, c.FieldId) };

        // Linked rows: the parent's key when it shows one, else the stored value itself.
        var linkedValue = Clone(c, hasKey ? standIn : c.FieldId);
        if (!hasOriginal) return new FilterNode { Condition = linkedValue };

        var linked = new FilterGroup
        {
            Logic = "and",
            Nodes = [Cond(c.FieldId, "isNotEmpty"), new FilterNode { Condition = linkedValue }],
        };
        var unlinked = new FilterGroup
        {
            Logic = "and",
            Nodes = [Cond(c.FieldId, "isEmpty"), new FilterNode { Condition = Clone(c, originalFid) }],
        };
        return new FilterNode
        {
            Group = new FilterGroup { Logic = "or", Nodes = [new FilterNode { Group = linked }, new FilterNode { Group = unlinked }] },
        };
    }

    private static FilterNode Cond(long fieldId, string op) =>
        new() { Condition = new FilterCondition { FieldId = fieldId, Operator = op } };

    private static FilterCondition Clone(FilterCondition c, long fieldId) => new()
    {
        FieldId = fieldId, Operator = c.Operator, Value = c.Value, SubField = c.SubField, ValueMode = c.ValueMode, ValueFieldId = c.ValueFieldId,
    };
}
