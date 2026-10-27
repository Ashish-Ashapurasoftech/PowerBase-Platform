using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Records;

/// <summary>
/// The row Ids of a table the current user may view under the role's View rules (Custom Rules and Own records),
/// for endpoints that read values across many records without returning the records themselves — filter
/// dropdowns (distinct values) and reference pickers (parent options). Conditions on physical fields run in SQL;
/// conditions on Formula/Lookup/Summary fields have no column, so they are evaluated in memory, as in GetRecord.
/// </summary>
public static class ViewableRowIds
{
    /// <summary>Null when the user may view every row (no restriction to apply).</summary>
    public static async Task<IReadOnlyCollection<long>?> ResolveAsync(
        TableAccessContext access, AppTable table, IReadOnlyList<AppField> fields,
        IRecordRepository recordRepo, IRelationalProjector relationalProjector, IFormulaProjector formulaProjector,
        CancellationToken ct = default)
    {
        if (access.Unrestricted) return null;
        if (!access.CanView) return [];
        if (access.ViewFilter is null && access.RestrictToCreatedBy is null) return null;

        var formulaFids = fields
            .Where(f => f.Fid.HasValue && FormulaTypeMap.IsComputedField(f.TypeCode, f.Settings))
            .Select(f => (long)f.Fid!.Value)
            .ToHashSet();
        var (physical, formula) = FormulaFilterSorter.SplitFilterTree(access.ViewFilter, formulaFids);

        // Fast bounded path: if the filter contains only physical fields (or no filter), query only row IDs directly
        // with a 30-second timeout. Avoids loading 50,000 full entities and decrypting unused columns into memory.
        if (formula is null)
        {
            return await recordRepo.ListMatchingRowIdsAsync(
                table, fields, physical, restrictToCreatedBy: access.RestrictToCreatedBy, maxRows: 10000, timeoutSeconds: 30, ct: ct);
        }

        // Formula conditions require in-memory evaluation: select only fields needed by formula/physical filter.
        var neededFids = new HashSet<long>();
        CollectReferencedFids(access.ViewFilter, neededFids);
        var minimalFields = fields.Where(f => f.Fid.HasValue && (neededFids.Contains(f.Fid.Value) || f.IsSystem)).ToList();
        if (minimalFields.Count == 0) minimalFields = fields.ToList();

        var rows = await recordRepo.ListAllMatchingAsync(
            table, minimalFields, physical, restrictToCreatedBy: access.RestrictToCreatedBy, ct: ct);
        if (rows.Count > 0)
        {
            var relational = await relationalProjector.ProjectAsync(table, minimalFields, rows, ct);
            var computed = formulaProjector.Project(minimalFields, rows, relational, table);
            rows = FormulaFilterSorter.ApplyFormulaFilters(rows.Zip(computed, (r, c) => (r, c)), formula, minimalFields)
                .Select(p => p.Row)
                .ToList();
        }

        return rows
            .Select(r => r.TryGetValue("Id", out var id) && id is not null ? Convert.ToInt64(id) : (long?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToList();
    }

    private static void CollectReferencedFids(PowerBase.Application.Reports.FilterGroup? group, HashSet<long> fids)
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
