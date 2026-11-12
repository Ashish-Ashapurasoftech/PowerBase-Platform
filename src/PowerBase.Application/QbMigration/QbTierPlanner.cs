namespace PowerBase.Application.QbMigration;

/// <summary>Load order for tables. PowerBase references have no foreign key and keep the QuickBase Record ID, so most
/// tables can load in any order. Order matters only where a child's reference is translated through a parent key
/// (the parent must be loaded first). Tables in a cycle of such references can't be ordered: they are returned so the
/// caller blocks them rather than loading a child before its parent.</summary>
public static class QbTierPlanner
{
    /// <param name="tables">All table ids.</param>
    /// <param name="mustFollow">(child, parent) pairs: the child loads after the parent.</param>
    public static (IReadOnlyDictionary<long, int> Tiers, IReadOnlyList<long> Cyclic) Plan(IEnumerable<long> tables, IEnumerable<(long Child, long Parent)> mustFollow)
    {
        var all = tables.ToHashSet();
        var parents = all.ToDictionary(t => t, _ => new HashSet<long>());
        foreach (var (child, parent) in mustFollow)
            if (child != parent && all.Contains(child) && all.Contains(parent)) parents[child].Add(parent);

        var tiers = new Dictionary<long, int>();
        var remaining = new HashSet<long>(all);
        for (var tier = 1; remaining.Count > 0; tier++)
        {
            var ready = remaining.Where(t => parents[t].All(tiers.ContainsKey)).ToList();
            if (ready.Count == 0) break;
            foreach (var t in ready) { tiers[t] = tier; remaining.Remove(t); }
        }
        return (tiers, remaining.OrderBy(t => t).ToList());
    }
}
