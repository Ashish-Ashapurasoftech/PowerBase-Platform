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

        var rows = await recordRepo.ListAllMatchingAsync(
            table, fields, physical, restrictToCreatedBy: access.RestrictToCreatedBy, ct: ct);
        if (formula is not null && rows.Count > 0)
        {
            var relational = await relationalProjector.ProjectAsync(table, fields, rows, ct);
            var computed = formulaProjector.Project(fields, rows, relational, table);
            rows = FormulaFilterSorter.ApplyFormulaFilters(rows.Zip(computed, (r, c) => (r, c)), formula, fields)
                .Select(p => p.Row)
                .ToList();
        }

        return rows
            .Select(r => r.TryGetValue("Id", out var id) && id is not null ? Convert.ToInt64(id) : (long?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToList();
    }
}
