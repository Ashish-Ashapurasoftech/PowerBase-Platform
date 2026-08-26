using PowerBase.Application.Reports.Commands.CreateReport;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Reports.Validation;

/// <summary>
/// Chart reports use: FilterTree, GroupByFieldId/GroupByMode (category/X-axis — required;
/// for Gauge the wizard sends GaugeFieldId's value here too, since Gauge has no real category
/// axis but RunSummaryAsync still needs a GroupByFieldId to group by), Aggregations (Y-values),
/// Chart (required, chart-type-specific settings), dynamic-filter fields. They do NOT use
/// Columns or SortFields (RunSummaryAsync ignores both for every report type it handles;
/// Chart's own SortBy/SortDirection live inside Chart, not the top-level SortFields list) or
/// HideTotals/GroupDefaultCollapsed/GroupByDescending (Table/Summary grouping-DISPLAY concepts
/// that don't apply to chart rendering).
/// </summary>
public sealed class ChartReportConfigValidator : IReportConfigValidator
{
    private static readonly HashSet<string> AllowedChartTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Bar", "StackedBar", "HorizontalBar", "HorizontalStackedBar",
        "Line", "LineBarCombo", "Pie", "Donut", "Gauge", "Waterfall", "Radial",
    };

    /// <summary>Chart types with no "Series / Group by" crosstab dimension — mirrors the
    /// frontend's chartSupportsSeries getter (pb-report-wizard-dialog.component.ts).</summary>
    private static readonly HashSet<string> NoSeriesChartTypes = new(StringComparer.OrdinalIgnoreCase)
        { "Pie", "Donut", "Gauge", "Waterfall" };

    private static readonly HashSet<string> AllowedSortBy = new(StringComparer.OrdinalIgnoreCase) { "Labels", "Values" };
    private static readonly HashSet<string> AllowedDirection = new(StringComparer.OrdinalIgnoreCase) { "Asc", "Desc" };
    private static readonly HashSet<string> AllowedGroupByMode = new(StringComparer.OrdinalIgnoreCase)
        { "EqualValues", "FirstWord", "FirstLetter" };
    private static readonly HashSet<string> AllowedDataLabelDisplayAs = new(StringComparer.OrdinalIgnoreCase)
        { "Value", "PercentOfSeries" };
    private static readonly HashSet<string> AllowedGaugeGoalType = new(StringComparer.OrdinalIgnoreCase) { "Fixed", "DataValue" };
    private static readonly HashSet<string> AllowedGaugeGoalFunction = new(StringComparer.OrdinalIgnoreCase) { "Sum", "Avg" };

    public string ReportType => "Chart";

    public IDictionary<string, string[]> Validate(ReportConfigValidationInput input, IReadOnlyList<AppField> tableFields)
    {
        var errors = new Dictionary<string, string[]>();
        var validFieldIds = CommonReportValidationHelpers.GetValidFieldIds(tableFields);

        CommonReportValidationHelpers.ValidateFilterGroup(input.FilterTree, validFieldIds, errors);
        CommonReportValidationHelpers.ValidateDynamicFilterFields(input, validFieldIds, errors);
        CommonReportValidationHelpers.ValidateAggregations(input.Aggregations, validFieldIds, errors);

        CommonReportValidationHelpers.RequirePopulated(input.GroupByFieldId.HasValue, "groupByFieldId", "Chart", errors);
        if (input.GroupByFieldId.HasValue && !validFieldIds.Contains(input.GroupByFieldId.Value))
            CommonReportValidationHelpers.AddError(errors, "groupByFieldId", $"Unknown field ID: {input.GroupByFieldId.Value}");
        if (!string.IsNullOrWhiteSpace(input.GroupByMode) && !AllowedGroupByMode.Contains(input.GroupByMode))
            CommonReportValidationHelpers.AddError(errors, "groupByMode", $"groupByMode must be one of: {string.Join(", ", AllowedGroupByMode)}");

        CommonReportValidationHelpers.ForbidIfPopulated(input.Columns.Count > 0, "columns", "Chart", errors);
        CommonReportValidationHelpers.ForbidIfPopulated(input.TableSortGroup.Count > 0, "tableSortGroup", "Chart", errors);
        CommonReportValidationHelpers.ForbidIfPopulated(input.Options is not null, "options", "Chart", errors);

        CommonReportValidationHelpers.RequirePopulated(input.Chart is not null, "chart", "Chart", errors);
        if (input.Chart is not null)
            ValidateChart(input.Chart, validFieldIds, errors);

        return errors;
    }

    private static void ValidateChart(ChartConfigCommand chart, HashSet<long> validFieldIds, IDictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(chart.ChartType) || !AllowedChartTypes.Contains(chart.ChartType))
        {
            CommonReportValidationHelpers.AddError(errors, "chart.chartType",
                $"Chart type must be one of: {string.Join(", ", AllowedChartTypes)}");
            return; // per-type rules below all key off a valid ChartType
        }

        if (chart.SeriesFieldId is { } seriesFieldId)
        {
            if (!validFieldIds.Contains(seriesFieldId))
                CommonReportValidationHelpers.AddError(errors, "chart.seriesFieldId", $"Unknown field ID: {seriesFieldId}");
            if (NoSeriesChartTypes.Contains(chart.ChartType))
                CommonReportValidationHelpers.AddError(errors, "chart.seriesFieldId", $"Series is not applicable to {chart.ChartType} charts.");
        }
        if (!string.IsNullOrWhiteSpace(chart.SeriesMode) && !AllowedGroupByMode.Contains(chart.SeriesMode))
            CommonReportValidationHelpers.AddError(errors, "chart.seriesMode", $"seriesMode must be one of: {string.Join(", ", AllowedGroupByMode)}");

        if (!string.IsNullOrWhiteSpace(chart.SortBy) && !AllowedSortBy.Contains(chart.SortBy))
            CommonReportValidationHelpers.AddError(errors, "chart.sortBy", $"sortBy must be one of: {string.Join(", ", AllowedSortBy)}");
        if (!string.IsNullOrWhiteSpace(chart.SortDirection) && !AllowedDirection.Contains(chart.SortDirection))
            CommonReportValidationHelpers.AddError(errors, "chart.sortDirection", $"sortDirection must be one of: {string.Join(", ", AllowedDirection)}");

        if (!string.IsNullOrWhiteSpace(chart.DataLabelDisplayAs) && !AllowedDataLabelDisplayAs.Contains(chart.DataLabelDisplayAs))
            CommonReportValidationHelpers.AddError(errors, "chart.dataLabelDisplayAs",
                $"dataLabelDisplayAs must be one of: {string.Join(", ", AllowedDataLabelDisplayAs)}");

        foreach (var secondaryId in chart.SecondaryAxisAggregationFieldIds ?? [])
        {
            if (!validFieldIds.Contains(secondaryId))
                CommonReportValidationHelpers.AddError(errors, "chart.secondaryAxisAggregationFieldIds", $"Unknown field ID: {secondaryId}");
        }

        var isGauge = string.Equals(chart.ChartType, "Gauge", StringComparison.OrdinalIgnoreCase);
        if (isGauge)
        {
            CommonReportValidationHelpers.RequirePopulated(chart.GaugeFieldId.HasValue, "chart.gaugeFieldId", "Gauge", errors);
            if (chart.GaugeFieldId.HasValue && !validFieldIds.Contains(chart.GaugeFieldId.Value))
                CommonReportValidationHelpers.AddError(errors, "chart.gaugeFieldId", $"Unknown field ID: {chart.GaugeFieldId.Value}");

            var goalType = string.IsNullOrWhiteSpace(chart.GaugeGoalType) ? "Fixed" : chart.GaugeGoalType;
            if (!AllowedGaugeGoalType.Contains(goalType))
                CommonReportValidationHelpers.AddError(errors, "chart.gaugeGoalType", $"gaugeGoalType must be one of: {string.Join(", ", AllowedGaugeGoalType)}");

            if (string.Equals(goalType, "DataValue", StringComparison.OrdinalIgnoreCase))
            {
                CommonReportValidationHelpers.RequirePopulated(chart.GaugeGoalFieldId.HasValue, "chart.gaugeGoalFieldId",
                    "Gauge with a Data Value goal", errors);
                if (chart.GaugeGoalFieldId.HasValue && !validFieldIds.Contains(chart.GaugeGoalFieldId.Value))
                    CommonReportValidationHelpers.AddError(errors, "chart.gaugeGoalFieldId", $"Unknown field ID: {chart.GaugeGoalFieldId.Value}");
                if (string.IsNullOrWhiteSpace(chart.GaugeGoalFunction) || !AllowedGaugeGoalFunction.Contains(chart.GaugeGoalFunction))
                    CommonReportValidationHelpers.AddError(errors, "chart.gaugeGoalFunction",
                        $"gaugeGoalFunction must be one of: {string.Join(", ", AllowedGaugeGoalFunction)} when gaugeGoalType is DataValue.");
            }
        }
        else
        {
            CommonReportValidationHelpers.ForbidIfPopulated(chart.GaugeFieldId.HasValue, "chart.gaugeFieldId", "non-Gauge charts", errors);
            CommonReportValidationHelpers.ForbidIfPopulated(chart.GaugeGoalFieldId.HasValue, "chart.gaugeGoalFieldId", "non-Gauge charts", errors);
        }
    }
}
