using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Validation;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Fields.TypeConversion;

/// <summary>
/// After a field's type changes, a saved report can hold settings the new type can't support — "Sum of Salary"
/// once Salary is Text, or numeric grouping ("Increment100") on a field that is now a date. Quickbase keeps the
/// saved report and simply ignores those parts while running it; changing the type back brings them back.
///
/// This applies that to a report definition that was just loaded (in memory only — it never saves anything):
///  • totals (Summarize-By) the field's current type doesn't allow are dropped, with the summary sort and chart
///    secondary-axis entries that pointed at them;
///  • a grouping mode the current type doesn't allow falls back to "EqualValues".
/// Same type rules the report validators use on save (FieldTypeCategoryHelper, GroupByModeCategoryHelper).
/// Only fields with a physical column are touched: Lookup/Summary/Formula values are computed in memory and keep
/// their existing behaviour. A definition that already fits its fields is returned unchanged.
/// </summary>
public static class ReportDefinitionTypeSanitizer
{
    private const string DefaultGroupMode = "EqualValues";

    /// <returns>True when something was ignored.</returns>
    public static bool Apply(ReportDefinition definition, IReadOnlyList<AppField> fields)
    {
        var byFid = new Dictionary<long, AppField>();
        foreach (var f in fields)
            if (f.Fid is int fid && !PhysicalNaming.IsComputedTypeCode(f.TypeCode)) byFid[fid] = f;

        var changed = false;

        // Totals.
        var removed = definition.Aggregations.Where(a => !AggregationFits(a, byFid)).ToList();
        if (removed.Count > 0)
        {
            changed = true;
            definition.Aggregations = definition.Aggregations.Except(removed).ToList();
            definition.SummarySortFields = definition.SummarySortFields
                .Where(s => s.Target != "Aggregation" || definition.Aggregations.Any(a =>
                    a.FieldId == s.AggregationFieldId && string.Equals(a.Function, s.AggregationFunction, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (definition.Chart is { } c)
                c.SecondaryAxisAggregationFieldIds = c.SecondaryAxisAggregationFieldIds
                    .Where(id => definition.Aggregations.Any(a => a.FieldId == id)).ToList();
        }

        // Grouping modes.
        if (definition.GroupByFieldId is { } groupFid && FixMode(groupFid, definition.GroupByMode, byFid) is { } groupMode)
        {
            definition.GroupByMode = groupMode;
            changed = true;
        }
        foreach (var level in definition.RowGroupLevels)
            if (FixMode(level.FieldId, level.GroupByMode, byFid) is { } mode) { level.GroupByMode = mode; changed = true; }
        foreach (var level in definition.TableSortGroup.Where(l => l.IsGroup))
            if (FixMode(level.FieldId, level.GroupByMode, byFid) is { } mode) { level.GroupByMode = mode; changed = true; }
        if (definition.Chart is { SeriesFieldId: { } seriesFid } chart && FixMode(seriesFid, chart.SeriesMode, byFid) is { } seriesMode)
        {
            chart.SeriesMode = seriesMode;
            changed = true;
        }

        return changed;
    }

    private static bool AggregationFits(SummaryAggregation aggregation, Dictionary<long, AppField> byFid)
    {
        // Unknown or computed field: not ours to judge.
        if (!byFid.TryGetValue(aggregation.FieldId, out var field)) return true;
        var known = FieldTypeCategoryHelper.NumericGroupFunctions.Contains(aggregation.Function, StringComparer.OrdinalIgnoreCase);
        if (!known) return true;
        return FieldTypeCategoryHelper.GetAllowedSummarizeByFunctions(field.TypeCode)
            .Contains(aggregation.Function, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The mode to use instead, or null when <paramref name="mode"/> already fits.</summary>
    private static string? FixMode(long fid, string? mode, Dictionary<long, AppField> byFid)
    {
        if (string.IsNullOrWhiteSpace(mode) || !byFid.TryGetValue(fid, out var field)) return null;
        var allowed = GroupByModeCategoryHelper.GetAllowedGroupByModes(field.TypeCode);
        return allowed.Contains(mode, StringComparer.OrdinalIgnoreCase) ? null : DefaultGroupMode;
    }
}
