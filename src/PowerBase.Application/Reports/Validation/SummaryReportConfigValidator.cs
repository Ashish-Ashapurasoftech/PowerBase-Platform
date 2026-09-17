using System.Text.Json;
using PowerBase.Application.Reports.Commands.CreateReport;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Reports.Validation;

/// <summary>
/// Summary reports use: FilterTree, GroupByFieldId/GroupByMode/GroupByDescending (Rows —
/// legacy single-level fallback, superseded by RowGroupLevels when that's non-empty; one of the
/// two is required, RunSummaryAsync returns an empty result without either), HideTotals,
/// GroupDefaultCollapsed, Aggregations (Summarize Data), SortFields, dynamic-filter fields.
/// They do NOT use Columns — RunSummaryAsync synthesizes its own output columns from the Rows
/// group level(s) + Aggregations and never reads definition.Columns.
/// RowGroupLevels is the chained "Group by X, then by Y, then by Z, ..." Rows list — an ordered,
/// same-field-allowed-twice list of group levels, all evaluated together in one query alongside
/// the optional crosstab dimension below (RunSummaryAsync/ExportSummaryAsync's SummarizeAsync
/// call takes every level as its groupByFields list).
/// Chart is null for most Summary reports, but may carry just SeriesFieldId/SeriesMode to
/// configure the "Group columns" crosstab dimension — RunSummaryAsync/ExportSummaryAsync
/// already read Chart?.SeriesFieldId/SeriesMode generically (they were built for Chart reports'
/// series-splitting, and Summary reuses the same mechanism verbatim, combinable with
/// RowGroupLevels). Every other ChartConfig sub-field (ChartType, axis/gauge/drilldown settings,
/// ...) is simply unused for Summary — not validated or rejected, since the client always sends
/// them at their non-nullable defaults.
/// </summary>
public sealed class SummaryReportConfigValidator : IReportConfigValidator
{
    /// <summary>Defensive sanity cap on the number of Rows group levels — not a product
    /// requirement, just a guard against a pathological payload (same convention as Table's
    /// TableSortGroup MaxSortGroupLevels).</summary>
    private const int MaxRowGroupLevels = 10;

    public string ReportType => "Summary";

    public IDictionary<string, string[]> Validate(ReportConfigValidationInput input, IReadOnlyList<AppField> tableFields)
    {
        var errors = new Dictionary<string, string[]>();
        var validFieldIds = CommonReportValidationHelpers.GetValidFieldIds(tableFields);
        var fieldMap = tableFields.Where(f => f.Fid.HasValue).ToDictionary(f => (long)f.Fid!.Value, f => f);

        CommonReportValidationHelpers.ValidateFilterGroup(input.FilterTree, validFieldIds, errors);
        CommonReportValidationHelpers.ValidateDynamicFilterFields(input, validFieldIds, errors);

        CommonReportValidationHelpers.RequirePopulated(
            input.GroupByFieldId.HasValue || input.RowGroupLevels.Count > 0, "groupByFieldId", "Summary", errors);

        if (input.RowGroupLevels.Count > 0)
        {
            ValidateRowGroupLevels(input.RowGroupLevels, validFieldIds, fieldMap, errors);
        }
        else if (input.GroupByFieldId.HasValue)
        {
            if (!validFieldIds.Contains(input.GroupByFieldId.Value))
                CommonReportValidationHelpers.AddError(errors, "groupByFieldId", $"Unknown field ID: {input.GroupByFieldId.Value}");
            if (!string.IsNullOrWhiteSpace(input.GroupByMode) && fieldMap.TryGetValue(input.GroupByFieldId.Value, out var groupByField))
            {
                var allowed = GroupByModeCategoryHelper.GetAllowedGroupByModes(groupByField.TypeCode);
                if (!allowed.Contains(input.GroupByMode, StringComparer.OrdinalIgnoreCase))
                    CommonReportValidationHelpers.AddError(errors, "groupByMode",
                        $"groupByMode must be one of: {string.Join(", ", allowed)} for field type '{groupByField.TypeCode}'.");
            }
        }

        ValidateAggregationsWithFieldTypeRules(input, fieldMap, errors);

        CommonReportValidationHelpers.ForbidIfPopulated(input.Columns.Count > 0, "columns", "Summary", errors);
        if (input.Chart is not null)
            ValidateCrosstab(input.Chart, validFieldIds, fieldMap, errors);
        CommonReportValidationHelpers.ForbidIfPopulated(input.TableSortGroup.Count > 0, "tableSortGroup", "Summary", errors);
        CommonReportValidationHelpers.ForbidIfPopulated(input.Options is not null, "options", "Summary", errors);

        return errors;
    }

    /// <summary>Same field ID is allowed to repeat across levels (e.g. "Category" as both the
    /// second and last "then by" level) — RunSummaryAsync keys each level by its position, not
    /// its field ID, so a repeat is meaningful (not ambiguous) data, not a validation error.</summary>
    private static void ValidateRowGroupLevels(
        List<RowGroupLevelCommand> levels, HashSet<long> validFieldIds, Dictionary<long, AppField> fieldMap, IDictionary<string, string[]> errors)
    {
        if (levels.Count > MaxRowGroupLevels)
            CommonReportValidationHelpers.AddError(errors, "rowGroupLevels", $"A report may have at most {MaxRowGroupLevels} Rows group levels.");

        foreach (var level in levels)
        {
            if (!validFieldIds.Contains(level.FieldId))
            {
                CommonReportValidationHelpers.AddError(errors, "rowGroupLevels", $"Unknown field ID: {level.FieldId}");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(level.GroupByMode) && fieldMap.TryGetValue(level.FieldId, out var field))
            {
                var allowed = GroupByModeCategoryHelper.GetAllowedGroupByModes(field.TypeCode);
                if (!allowed.Contains(level.GroupByMode, StringComparer.OrdinalIgnoreCase))
                    CommonReportValidationHelpers.AddError(errors, "rowGroupLevels",
                        $"groupByMode must be one of: {string.Join(", ", allowed)} for field type '{field.TypeCode}'.");
            }
        }
    }

    /// <summary>Validates only the crosstab-relevant slice of Chart (SeriesFieldId/SeriesMode) for
    /// a Summary report — every other ChartConfig sub-field is ignored (see class doc comment).</summary>
    private static void ValidateCrosstab(
        ChartConfigCommand chart, HashSet<long> validFieldIds, Dictionary<long, AppField> fieldMap, IDictionary<string, string[]> errors)
    {
        if (chart.SeriesFieldId is not { } seriesFieldId)
            return;

        if (!validFieldIds.Contains(seriesFieldId))
        {
            CommonReportValidationHelpers.AddError(errors, "chart.seriesFieldId", $"Unknown field ID: {seriesFieldId}");
            return;
        }

        if (!string.IsNullOrWhiteSpace(chart.SeriesMode) && fieldMap.TryGetValue(seriesFieldId, out var seriesField))
        {
            var allowed = GroupByModeCategoryHelper.GetAllowedGroupByModes(seriesField.TypeCode);
            if (!allowed.Contains(chart.SeriesMode, StringComparer.OrdinalIgnoreCase))
                CommonReportValidationHelpers.AddError(errors, "chart.seriesMode",
                    $"seriesMode must be one of: {string.Join(", ", allowed)} for field type '{seriesField.TypeCode}'.");
        }
    }

    /// <summary>Layers the field-type-conditional Summarize-By restriction (FieldTypeCategoryHelper)
    /// on top of the base function/field-id checks — e.g. rejects Sum on a Text field.</summary>
    internal static void ValidateAggregationsWithFieldTypeRules(
        ReportConfigValidationInput input, Dictionary<long, AppField> fieldMap, IDictionary<string, string[]> errors)
    {
        foreach (var agg in input.Aggregations)
        {
            if (!fieldMap.TryGetValue(agg.FieldId, out var field))
            {
                CommonReportValidationHelpers.AddError(errors, "aggregations", $"Unknown field ID in aggregation: {agg.FieldId}");
                continue;
            }

            var formulaResultType = GetFormulaResultType(field);
            var allowed = FieldTypeCategoryHelper.GetAllowedSummarizeByFunctions(field.TypeCode, formulaResultType);
            if (!allowed.Contains(agg.Function, StringComparer.OrdinalIgnoreCase))
            {
                var fieldLabel = string.IsNullOrWhiteSpace(field.Label) ? field.Name : field.Label;
                CommonReportValidationHelpers.AddError(errors, "aggregations",
                    $"'{agg.Function}' is not a valid Summarize-By option for field '{fieldLabel}' ({field.TypeCode}). Allowed: {string.Join(", ", allowed)}");
            }
        }
    }

    /// <summary>Parses FormulaSettings.ResultType out of AppField.Settings for the generic
    /// "Formula" TypeCode (tenants whose core.FieldType catalog lacks a dedicated Formula_{X}
    /// row). Returns null for every other TypeCode or on any parse failure.</summary>
    internal static string? GetFormulaResultType(AppField field)
    {
        if (!string.Equals(field.TypeCode, "Formula", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(field.Settings))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(field.Settings);
            if (doc.RootElement.TryGetProperty("resultType", out var rt) ||
                doc.RootElement.TryGetProperty("ResultType", out rt))
                return rt.GetString();
        }
        catch (JsonException)
        {
            // Malformed settings JSON — treated as "no result type", which routes to DistinctOnly.
        }

        return null;
    }
}
