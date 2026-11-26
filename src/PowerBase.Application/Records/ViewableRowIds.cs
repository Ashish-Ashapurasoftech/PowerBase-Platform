using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Relationships;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Records;

/// <summary>
/// Which rows of a table a request may read under a role's View rules (Custom Rules and Own records), in the form the
/// repository applies best. <see cref="Kind"/>:
/// <list type="bullet">
/// <item><c>All</c> — no restriction.</item>
/// <item><c>None</c> — the role sees no row.</item>
/// <item><c>Sql</c> — the rule runs in SQL: <see cref="Filter"/> (resolved against <see cref="Fields"/>, which hold the stand-ins of
/// Ready stored columns) plus <see cref="RestrictToCreatedBy"/>. Covers every row of any table, with no id list.</item>
/// <item><c>Ids</c> — the rule needed calculated values worked out in memory; <see cref="Ids"/> is the complete list.</item>
/// </list>
/// </summary>
public sealed record ViewRowScope(
    ViewRowScopeKind Kind,
    FilterGroup? Filter = null,
    IReadOnlyList<AppField>? Fields = null,
    long? RestrictToCreatedBy = null,
    IReadOnlyCollection<long>? Ids = null)
{
    public static readonly ViewRowScope All = new(ViewRowScopeKind.All);
    public static readonly ViewRowScope None = new(ViewRowScopeKind.None);
}

public enum ViewRowScopeKind { All, None, Sql, Ids }

/// <summary>
/// The rows of a table the current user may view under the role's View rules, for endpoints that read values across many
/// records without returning the records themselves — filter dropdowns (distinct values) and reference pickers (parent options).
/// Conditions on physical fields and on Ready stored calculated fields (c_{fid}) run in SQL; conditions on any other Formula /
/// Lookup / Summary field have no column, so they are evaluated in memory over at most <see cref="InMemoryReadLimit.Rows"/>
/// rows — past that the request is refused (<see cref="Domain.Exceptions.ResultTooLargeException"/>), never answered from
/// the first rows only.
/// </summary>
public static class ViewableRowIds
{
    /// <summary>The scope for <paramref name="access"/>: SQL whenever the rule can run there, a complete id list otherwise.</summary>
    public static async Task<ViewRowScope> ResolveScopeAsync(
        TableAccessContext access, AppTable table, IReadOnlyList<AppField> fields,
        IRecordRepository recordRepo, IRelationalProjector relationalProjector, IFormulaProjector formulaProjector,
        CancellationToken ct = default, IReadOnlySet<long>? readyFieldIds = null)
    {
        if (access.Unrestricted) return ViewRowScope.All;
        if (!access.CanView) return ViewRowScope.None;
        if (access.ViewFilter is null && access.RestrictToCreatedBy is null) return ViewRowScope.All;

        var formulaFids = fields
            .Where(f => f.Fid.HasValue && FormulaTypeMap.IsComputedField(f.TypeCode, f.Settings))
            .Select(f => (long)f.Fid!.Value)
            .ToHashSet();

        // Only physical conditions: the rule is SQL as it is.
        if (!FormulaFilterSorter.TreeContainsFormulaField(access.ViewFilter, formulaFids))
            return new ViewRowScope(ViewRowScopeKind.Sql, access.ViewFilter, fields, access.RestrictToCreatedBy);

        // A Ready stored field (c_{fid}) is a column now: when every calculated condition is on one, the whole rule runs in SQL
        // through the stand-ins, as in reports.
        if (readyFieldIds is { Count: > 0 })
        {
            var notStored = formulaFids.Except(Compute.MaterializedReadFields.ReadyFids(fields, readyFieldIds)).ToHashSet();
            if (!FormulaFilterSorter.TreeContainsFormulaField(access.ViewFilter, notStored))
                return new ViewRowScope(ViewRowScopeKind.Sql, access.ViewFilter,
                    Compute.MaterializedReadFields.Substitute(fields, readyFieldIds), access.RestrictToCreatedBy);
        }

        var (physical, formula) = FormulaFilterSorter.SplitFilterTree(access.ViewFilter, formulaFids);

        // Formula conditions require in-memory evaluation: select only fields needed by formula/physical filter.
        var neededFids = new HashSet<long>();
        CollectReferencedFids(access.ViewFilter, neededFids);
        var minimalFields = fields.Where(f => f.Fid.HasValue && (neededFids.Contains(f.Fid.Value) || f.IsSystem)).ToList();
        if (minimalFields.Count == 0) minimalFields = fields.ToList();

        // Every row, in Id batches: one batch in memory at a time, only the ids of the rows that pass are kept. No row limit.
        var ids = new List<long>();
        await foreach (var matches in MatchStream.MatchesAsync(recordRepo, relationalProjector, formulaProjector, table, minimalFields,
                           minimalFields, minimalFields, minimalFields, physical, formula, access.RestrictToCreatedBy, ct))
        {
            foreach (var (row, _) in matches)
                if (row.TryGetValue("Id", out var id) && id is not null) ids.Add(Convert.ToInt64(id));
        }
        return ids.Count == 0 ? ViewRowScope.None : new ViewRowScope(ViewRowScopeKind.Ids, Ids: ids);
    }

    /// <summary>
    /// The scope as a complete id list, for callers that need ids. Null when the user may view every row. A SQL scope's ids are
    /// read in keyset batches — every one, no limit (an id is 8 bytes; a million viewable rows is ~8 MB). Prefer
    /// <see cref="ResolveScopeAsync"/> and hand the SQL scope to the repository: then no id list is built at all.
    /// </summary>
    public static async Task<IReadOnlyCollection<long>?> ResolveAsync(
        TableAccessContext access, AppTable table, IReadOnlyList<AppField> fields,
        IRecordRepository recordRepo, IRelationalProjector relationalProjector, IFormulaProjector formulaProjector,
        CancellationToken ct = default, IReadOnlySet<long>? readyFieldIds = null)
    {
        var scope = await ResolveScopeAsync(access, table, fields, recordRepo, relationalProjector, formulaProjector, ct, readyFieldIds);
        switch (scope.Kind)
        {
            case ViewRowScopeKind.All: return null;
            case ViewRowScopeKind.None: return [];
            case ViewRowScopeKind.Ids: return scope.Ids!;
        }

        var ids = new List<long>();
        long afterId = 0;
        while (true)
        {
            var batch = await recordRepo.ListIdsAfterIdAsync(table, scope.Fields!, afterId, MatchStream.BatchSize * 10,
                scope.Filter, scope.RestrictToCreatedBy, ct);
            if (batch.Count == 0) break;
            ids.AddRange(batch.Select(b => b.Id));
            afterId = batch[^1].Id;
            if (batch.Count < MatchStream.BatchSize * 10) break;
        }
        return ids;
    }

    private static void CollectReferencedFids(FilterGroup? group, HashSet<long> fids)
    {
        if (group?.Nodes == null) return;
        foreach (var node in group.Nodes)
        {
            if (node.Condition != null)
            {
                fids.Add(node.Condition.FieldId);
                if (node.Condition.ValueFieldId.HasValue) fids.Add(node.Condition.ValueFieldId.Value);
            }
            if (node.Group != null) CollectReferencedFids(node.Group, fids);
        }
    }
}
