using System.Text.Json;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Reports.Validation;

/// <summary>
/// Summary reports use: FilterTree, GroupByFieldId/GroupByMode/GroupByDescending (Rows —
/// required, RunSummaryAsync returns an empty result without it), HideTotals,
/// GroupDefaultCollapsed, Aggregations (Summarize Data), SortFields, dynamic-filter fields.
/// They do NOT use Columns — RunSummaryAsync synthesizes its own output columns from
/// GroupByField + Aggregations and never reads definition.Columns.
/// Chart is currently null for every genuine Summary report (the crosstab/drilldown reuse of
/// ChartConfig.SeriesFieldId/SeriesMode/DrilldownReportId is a Phase 2 fix, not live yet) — once
/// that lands this validator's Chart rule relaxes to allow exactly those three sub-fields.
/// </summary>
public sealed class SummaryReportConfigValidator : IReportConfigValidator
{
    public string ReportType => "Summary";

    public IDictionary<string, string[]> Validate(ReportConfigValidationInput input, IReadOnlyList<AppField> tableFields)
    {
        var errors = new Dictionary<string, string[]>();
        var validFieldIds = CommonReportValidationHelpers.GetValidFieldIds(tableFields);
        var fieldMap = tableFields.Where(f => f.Fid.HasValue).ToDictionary(f => (long)f.Fid!.Value, f => f);

        CommonReportValidationHelpers.ValidateFilterGroup(input.FilterTree, validFieldIds, errors);
        CommonReportValidationHelpers.ValidateDynamicFilterFields(input, validFieldIds, errors);

        CommonReportValidationHelpers.RequirePopulated(input.GroupByFieldId.HasValue, "groupByFieldId", "Summary", errors);
        if (input.GroupByFieldId.HasValue && !validFieldIds.Contains(input.GroupByFieldId.Value))
            CommonReportValidationHelpers.AddError(errors, "groupByFieldId", $"Unknown field ID: {input.GroupByFieldId.Value}");
        if (!string.IsNullOrWhiteSpace(input.GroupByMode) && input.GroupByFieldId.HasValue
            && fieldMap.TryGetValue(input.GroupByFieldId.Value, out var groupByField))
        {
            var allowed = GroupByModeCategoryHelper.GetAllowedGroupByModes(groupByField.TypeCode);
            if (!allowed.Contains(input.GroupByMode, StringComparer.OrdinalIgnoreCase))
                CommonReportValidationHelpers.AddError(errors, "groupByMode",
                    $"groupByMode must be one of: {string.Join(", ", allowed)} for field type '{groupByField.TypeCode}'.");
        }

        ValidateAggregationsWithFieldTypeRules(input, fieldMap, errors);

        CommonReportValidationHelpers.ForbidIfPopulated(input.Columns.Count > 0, "columns", "Summary", errors);
        CommonReportValidationHelpers.ForbidIfPopulated(input.Chart is not null, "chart", "Summary", errors);
        CommonReportValidationHelpers.ForbidIfPopulated(input.TableSortGroup.Count > 0, "tableSortGroup", "Summary", errors);
        CommonReportValidationHelpers.ForbidIfPopulated(input.Options is not null, "options", "Summary", errors);

        return errors;
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
