using FluentAssertions;
using PowerBase.Application.Reports.Commands.CreateReport;
using PowerBase.Application.Reports.Validation;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Reports;

/// <summary>Direct unit tests for the Phase 0/1 per-report-type validators — each report type
/// must only allow and validate the settings applicable to that type (per the original
/// requirement), enforced independently of frontend validation.</summary>
public class ReportConfigValidatorTests
{
    private static AppField MakeField(long fid, string typeCode = "Text", string? settings = null) => new()
    {
        Id = fid, Fid = (int)fid, Name = $"Field{fid}", TypeCode = typeCode, IsReportable = true, Settings = settings,
    };

    private static ReportConfigValidationInput EmptyInput(
        List<long>? columns = null,
        long? groupByFieldId = null,
        List<SummaryAggregationCommand>? aggregations = null,
        ChartConfigCommand? chart = null,
        List<SortGroupLevelCommand>? tableSortGroup = null,
        ReportOptionsCommand? options = null,
        List<RowGroupLevelCommand>? rowGroupLevels = null,
        List<SummarySortFieldCommand>? summarySortFields = null) => new()
    {
        Columns = columns ?? [],
        SortFields = [],
        GroupByFieldId = groupByFieldId,
        Aggregations = aggregations ?? [],
        CustomDynamicFilterFields = [],
        Chart = chart,
        TableSortGroup = tableSortGroup ?? [],
        Options = options,
        RowGroupLevels = rowGroupLevels ?? [],
        SummarySortFields = summarySortFields ?? [],
    };

    // ── Table ────────────────────────────────────────────────────────────────

    [Fact]
    public void Table_WithPopulatedAggregations_IsRejected()
    {
        var sut = new TableReportConfigValidator();
        var fields = new List<AppField> { MakeField(1) };

        var errors = sut.Validate(EmptyInput(aggregations: [new SummaryAggregationCommand(1, "Sum")]), fields);

        errors.Should().ContainKey("aggregations");
    }

    [Fact]
    public void Table_WithPopulatedChart_IsRejected()
    {
        var sut = new TableReportConfigValidator();
        var fields = new List<AppField> { MakeField(1) };
        var chart = new ChartConfigCommand("Bar", null, "EqualValues", null, null, null, null, false, "Labels", "Asc", null, null, false, false, null);

        var errors = sut.Validate(EmptyInput(chart: chart), fields);

        errors.Should().ContainKey("chart");
    }

    [Fact]
    public void Table_PlainConfig_IsAccepted()
    {
        var sut = new TableReportConfigValidator();
        var fields = new List<AppField> { MakeField(1) };

        var errors = sut.Validate(EmptyInput(columns: [1L]), fields);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Table_WithUnknownFieldInTableSortGroup_IsRejected()
    {
        var sut = new TableReportConfigValidator();
        var fields = new List<AppField> { MakeField(1) };

        var errors = sut.Validate(EmptyInput(tableSortGroup: [new SortGroupLevelCommand(999, false, true)]), fields);

        errors.Should().ContainKey("tableSortGroup");
    }

    [Fact]
    public void Table_WithInvalidColumnHeaderTextOption_IsRejected()
    {
        var sut = new TableReportConfigValidator();
        var fields = new List<AppField> { MakeField(1) };

        var errors = sut.Validate(EmptyInput(options: new ReportOptionsCommand(ColumnHeaderText: "Bogus")), fields);

        errors.Should().ContainKey("options.columnHeaderText");
    }

    // ── Summary ──────────────────────────────────────────────────────────────

    [Fact]
    public void Summary_WithoutGroupByFieldId_IsRejected()
    {
        var sut = new SummaryReportConfigValidator();
        var fields = new List<AppField> { MakeField(1) };

        var errors = sut.Validate(EmptyInput(), fields);

        errors.Should().ContainKey("groupByFieldId");
    }

    [Fact]
    public void Summary_WithPopulatedColumns_IsRejected()
    {
        var sut = new SummaryReportConfigValidator();
        var fields = new List<AppField> { MakeField(1) };

        var errors = sut.Validate(EmptyInput(columns: [1L], groupByFieldId: 1), fields);

        errors.Should().ContainKey("columns");
    }

    [Fact]
    public void Summary_WithValidCrosstabSeriesField_IsAccepted()
    {
        var sut = new SummaryReportConfigValidator();
        var fields = new List<AppField> { MakeField(1), MakeField(2) };
        var chart = new ChartConfigCommand("Bar", 2, "EqualValues", null, null, null, null, false, "Labels", "Asc", null, null, false, false, null);

        var errors = sut.Validate(EmptyInput(groupByFieldId: 1, chart: chart), fields);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Summary_WithUnknownCrosstabSeriesField_IsRejected()
    {
        var sut = new SummaryReportConfigValidator();
        var fields = new List<AppField> { MakeField(1) };
        var chart = new ChartConfigCommand("Bar", 999, "EqualValues", null, null, null, null, false, "Labels", "Asc", null, null, false, false, null);

        var errors = sut.Validate(EmptyInput(groupByFieldId: 1, chart: chart), fields);

        errors.Should().ContainKey("chart.seriesFieldId");
    }

    [Fact]
    public void Summary_WithInvalidCrosstabSeriesMode_IsRejected()
    {
        var sut = new SummaryReportConfigValidator();
        var fields = new List<AppField> { MakeField(1), MakeField(2, "Number") };
        var chart = new ChartConfigCommand("Bar", 2, "FirstWord", null, null, null, null, false, "Labels", "Asc", null, null, false, false, null);

        var errors = sut.Validate(EmptyInput(groupByFieldId: 1, chart: chart), fields);

        errors.Should().ContainKey("chart.seriesMode");
    }

    [Fact]
    public void Summary_WithRowGroupLevels_NoLegacyGroupByFieldId_IsAccepted()
    {
        var sut = new SummaryReportConfigValidator();
        var fields = new List<AppField> { MakeField(1), MakeField(2) };
        var levels = new List<RowGroupLevelCommand> { new(1), new(2) };

        var errors = sut.Validate(EmptyInput(rowGroupLevels: levels), fields);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Summary_WithRowGroupLevels_SameFieldTwice_IsAccepted()
    {
        // Rows allows the same field to appear at more than one level (e.g. "Category" as both
        // level 2 and the last level) — RunSummaryAsync keys each level by position, not field ID.
        var sut = new SummaryReportConfigValidator();
        var fields = new List<AppField> { MakeField(1), MakeField(2) };
        var levels = new List<RowGroupLevelCommand> { new(1), new(2), new(1) };

        var errors = sut.Validate(EmptyInput(rowGroupLevels: levels), fields);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Summary_WithUnknownFieldInRowGroupLevels_IsRejected()
    {
        var sut = new SummaryReportConfigValidator();
        var fields = new List<AppField> { MakeField(1) };
        var levels = new List<RowGroupLevelCommand> { new(1), new(999) };

        var errors = sut.Validate(EmptyInput(rowGroupLevels: levels), fields);

        errors.Should().ContainKey("rowGroupLevels");
    }

    [Fact]
    public void Summary_WithInvalidGroupByModeInRowGroupLevels_IsRejected()
    {
        var sut = new SummaryReportConfigValidator();
        var fields = new List<AppField> { MakeField(1, "Number") };
        var levels = new List<RowGroupLevelCommand> { new(1, "FirstWord") };

        var errors = sut.Validate(EmptyInput(rowGroupLevels: levels), fields);

        errors.Should().ContainKey("rowGroupLevels");
    }

    [Fact]
    public void Summary_WithTooManyRowGroupLevels_IsRejected()
    {
        var sut = new SummaryReportConfigValidator();
        var fields = new List<AppField> { MakeField(1) };
        var levels = Enumerable.Range(0, 11).Select(_ => new RowGroupLevelCommand(1)).ToList();

        var errors = sut.Validate(EmptyInput(rowGroupLevels: levels), fields);

        errors.Should().ContainKey("rowGroupLevels");
    }

    [Fact]
    public void Table_WithPopulatedRowGroupLevels_IsRejected()
    {
        var sut = new TableReportConfigValidator();
        var fields = new List<AppField> { MakeField(1) };

        var errors = sut.Validate(EmptyInput(rowGroupLevels: [new RowGroupLevelCommand(1)]), fields);

        errors.Should().ContainKey("rowGroupLevels");
    }

    [Fact]
    public void Chart_WithPopulatedRowGroupLevels_IsRejected()
    {
        var sut = new ChartReportConfigValidator();
        var fields = new List<AppField> { MakeField(1) };
        var chart = new ChartConfigCommand("Bar", null, "EqualValues", null, null, null, null, false, "Labels", "Asc", null, null, false, false, null);

        var errors = sut.Validate(EmptyInput(groupByFieldId: 1, chart: chart, rowGroupLevels: [new RowGroupLevelCommand(1)]), fields);

        errors.Should().ContainKey("rowGroupLevels");
    }

    [Fact]
    public void Summary_WithValidRowLevelSort_IsAccepted()
    {
        var sut = new SummaryReportConfigValidator();
        var fields = new List<AppField> { MakeField(1) };
        var sort = new List<SummarySortFieldCommand> { new("RowLevel", 0, null, null, true) };

        var errors = sut.Validate(EmptyInput(groupByFieldId: 1, summarySortFields: sort), fields);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Summary_WithValidAggregationSort_IsAccepted()
    {
        var sut = new SummaryReportConfigValidator();
        var fields = new List<AppField> { MakeField(1), MakeField(2, "Number") };
        var aggregations = new List<SummaryAggregationCommand> { new(2, "Sum") };
        var sort = new List<SummarySortFieldCommand> { new("Aggregation", null, 2, "Sum", false) };

        var errors = sut.Validate(EmptyInput(groupByFieldId: 1, aggregations: aggregations, summarySortFields: sort), fields);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Summary_WithSortByCount_IsAccepted()
    {
        var sut = new SummaryReportConfigValidator();
        var fields = new List<AppField> { MakeField(1) };
        var sort = new List<SummarySortFieldCommand> { new("Count", null, null, null, false) };

        var errors = sut.Validate(EmptyInput(groupByFieldId: 1, summarySortFields: sort), fields);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Summary_WithOutOfRangeRowLevelIndex_IsRejected()
    {
        var sut = new SummaryReportConfigValidator();
        var fields = new List<AppField> { MakeField(1) };
        var sort = new List<SummarySortFieldCommand> { new("RowLevel", 1, null, null, false) };

        var errors = sut.Validate(EmptyInput(groupByFieldId: 1, summarySortFields: sort), fields);

        errors.Should().ContainKey("summarySortFields");
    }

    [Fact]
    public void Summary_WithSortByUnconfiguredAggregation_IsRejected()
    {
        var sut = new SummaryReportConfigValidator();
        var fields = new List<AppField> { MakeField(1), MakeField(2, "Number") };
        // Aggregation configured is Sum, but the sort asks for Avg of the same field — no match.
        var aggregations = new List<SummaryAggregationCommand> { new(2, "Sum") };
        var sort = new List<SummarySortFieldCommand> { new("Aggregation", null, 2, "Avg", false) };

        var errors = sut.Validate(EmptyInput(groupByFieldId: 1, aggregations: aggregations, summarySortFields: sort), fields);

        errors.Should().ContainKey("summarySortFields");
    }

    [Fact]
    public void Summary_WithSortFieldsAndCrosstab_IsRejected()
    {
        var sut = new SummaryReportConfigValidator();
        var fields = new List<AppField> { MakeField(1), MakeField(2) };
        var chart = new ChartConfigCommand("Bar", 2, "EqualValues", null, null, null, null, false, "Labels", "Asc", null, null, false, false, null);
        var sort = new List<SummarySortFieldCommand> { new("RowLevel", 0, null, null, false) };

        var errors = sut.Validate(EmptyInput(groupByFieldId: 1, chart: chart, summarySortFields: sort), fields);

        errors.Should().ContainKey("summarySortFields");
    }

    [Fact]
    public void Table_WithPopulatedSummarySortFields_IsRejected()
    {
        var sut = new TableReportConfigValidator();
        var fields = new List<AppField> { MakeField(1) };
        var sort = new List<SummarySortFieldCommand> { new("Count", null, null, null, false) };

        var errors = sut.Validate(EmptyInput(summarySortFields: sort), fields);

        errors.Should().ContainKey("summarySortFields");
    }

    [Fact]
    public void Chart_WithPopulatedSummarySortFields_IsRejected()
    {
        var sut = new ChartReportConfigValidator();
        var fields = new List<AppField> { MakeField(1) };
        var chart = new ChartConfigCommand("Bar", null, "EqualValues", null, null, null, null, false, "Labels", "Asc", null, null, false, false, null);
        var sort = new List<SummarySortFieldCommand> { new("Count", null, null, null, false) };

        var errors = sut.Validate(EmptyInput(groupByFieldId: 1, chart: chart, summarySortFields: sort), fields);

        errors.Should().ContainKey("summarySortFields");
    }

    [Fact]
    public void Summary_SumOnTextField_IsRejected()
    {
        var sut = new SummaryReportConfigValidator();
        var fields = new List<AppField> { MakeField(1, "Text") };

        var errors = sut.Validate(EmptyInput(groupByFieldId: 1, aggregations: [new SummaryAggregationCommand(1, "Sum")]), fields);

        errors.Should().ContainKey("aggregations");
    }

    [Fact]
    public void Summary_SumOnNumberField_IsAccepted()
    {
        var sut = new SummaryReportConfigValidator();
        var fields = new List<AppField> { MakeField(1, "Number") };

        var errors = sut.Validate(EmptyInput(groupByFieldId: 1, aggregations: [new SummaryAggregationCommand(1, "Sum")]), fields);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Summary_DistinctCountOnTextField_IsAccepted()
    {
        var sut = new SummaryReportConfigValidator();
        var fields = new List<AppField> { MakeField(1, "Text") };

        var errors = sut.Validate(EmptyInput(groupByFieldId: 1, aggregations: [new SummaryAggregationCommand(1, "DistinctCount")]), fields);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Summary_MaxOnDateField_IsAccepted()
    {
        var sut = new SummaryReportConfigValidator();
        var fields = new List<AppField> { MakeField(1, "Date") };

        var errors = sut.Validate(EmptyInput(groupByFieldId: 1, aggregations: [new SummaryAggregationCommand(1, "Max")]), fields);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Summary_SumOnDateField_IsRejected()
    {
        var sut = new SummaryReportConfigValidator();
        var fields = new List<AppField> { MakeField(1, "Date") };

        var errors = sut.Validate(EmptyInput(groupByFieldId: 1, aggregations: [new SummaryAggregationCommand(1, "Sum")]), fields);

        errors.Should().ContainKey("aggregations");
    }

    [Fact]
    public void Summary_SumOnNumericRangeField_IsRejected_PerProductDecision()
    {
        // NumericRange shares the "Numeric" core.FieldType category with Number/Currency/etc.,
        // but was explicitly decided NOT to inherit the numeric Summarize-By options.
        var sut = new SummaryReportConfigValidator();
        var fields = new List<AppField> { MakeField(1, "NumericRange") };

        var errors = sut.Validate(EmptyInput(groupByFieldId: 1, aggregations: [new SummaryAggregationCommand(1, "Sum")]), fields);

        errors.Should().ContainKey("aggregations");
    }

    [Fact]
    public void Summary_SumOnFormulaFieldWithNumericResultType_IsAccepted()
    {
        var sut = new SummaryReportConfigValidator();
        var fields = new List<AppField> { MakeField(1, "Formula", settings: """{"resultType":"Number"}""") };

        var errors = sut.Validate(EmptyInput(groupByFieldId: 1, aggregations: [new SummaryAggregationCommand(1, "Sum")]), fields);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Summary_SumOnFormulaFieldWithTextResultType_IsRejected()
    {
        var sut = new SummaryReportConfigValidator();
        var fields = new List<AppField> { MakeField(1, "Formula", settings: """{"resultType":"Text"}""") };

        var errors = sut.Validate(EmptyInput(groupByFieldId: 1, aggregations: [new SummaryAggregationCommand(1, "Sum")]), fields);

        errors.Should().ContainKey("aggregations");
    }

    // ── Chart ────────────────────────────────────────────────────────────────

    [Fact]
    public void Chart_WithoutChartConfig_IsRejected()
    {
        var sut = new ChartReportConfigValidator();
        var fields = new List<AppField> { MakeField(1) };

        var errors = sut.Validate(EmptyInput(groupByFieldId: 1), fields);

        errors.Should().ContainKey("chart");
    }

    [Fact]
    public void Chart_WithoutGroupByFieldId_IsRejected()
    {
        var sut = new ChartReportConfigValidator();
        var fields = new List<AppField> { MakeField(1) };
        var chart = new ChartConfigCommand("Bar", null, "EqualValues", null, null, null, null, false, "Labels", "Asc", null, null, false, false, null);

        var errors = sut.Validate(EmptyInput(chart: chart), fields);

        errors.Should().ContainKey("groupByFieldId");
    }

    [Fact]
    public void Chart_WithPopulatedColumns_IsRejected()
    {
        var sut = new ChartReportConfigValidator();
        var fields = new List<AppField> { MakeField(1) };
        var chart = new ChartConfigCommand("Bar", null, "EqualValues", null, null, null, null, false, "Labels", "Asc", null, null, false, false, null);

        var errors = sut.Validate(EmptyInput(columns: [1L], groupByFieldId: 1, chart: chart), fields);

        errors.Should().ContainKey("columns");
    }

    [Fact]
    public void Chart_ValidConfig_IsAccepted()
    {
        var sut = new ChartReportConfigValidator();
        var fields = new List<AppField> { MakeField(1) };
        var chart = new ChartConfigCommand("Bar", null, "EqualValues", null, null, null, null, false, "Labels", "Asc", null, null, false, false, null);

        var errors = sut.Validate(EmptyInput(groupByFieldId: 1, chart: chart), fields);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Chart_UnknownChartType_IsRejected()
    {
        var sut = new ChartReportConfigValidator();
        var fields = new List<AppField> { MakeField(1) };
        var chart = new ChartConfigCommand("Bogus", null, "EqualValues", null, null, null, null, false, "Labels", "Asc", null, null, false, false, null);

        var errors = sut.Validate(EmptyInput(groupByFieldId: 1, chart: chart), fields);

        errors.Should().ContainKey("chart.chartType");
    }

    [Fact]
    public void Chart_SeriesOnPieChart_IsRejected()
    {
        var sut = new ChartReportConfigValidator();
        var fields = new List<AppField> { MakeField(1), MakeField(2) };
        var chart = new ChartConfigCommand("Pie", 2, "EqualValues", null, null, null, null, false, "Labels", "Asc", null, null, false, false, null);

        var errors = sut.Validate(EmptyInput(groupByFieldId: 1, chart: chart), fields);

        errors.Should().ContainKey("chart.seriesFieldId");
    }

    [Fact]
    public void Chart_SeriesOnBarChart_IsAccepted()
    {
        var sut = new ChartReportConfigValidator();
        var fields = new List<AppField> { MakeField(1), MakeField(2) };
        var chart = new ChartConfigCommand("Bar", 2, "EqualValues", null, null, null, null, false, "Labels", "Asc", null, null, false, false, null);

        var errors = sut.Validate(EmptyInput(groupByFieldId: 1, chart: chart), fields);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Chart_SeriesWithMultipleAggregations_IsRejected()
    {
        // buildChartJsConfig (frontend) only ever plots the first aggregation once a series is
        // set, silently dropping the rest — reject the combination outright instead.
        var sut = new ChartReportConfigValidator();
        var fields = new List<AppField> { MakeField(1), MakeField(2), MakeField(3, "Number") };
        var chart = new ChartConfigCommand("Bar", 2, "EqualValues", null, null, null, null, false, "Labels", "Asc", null, null, false, false, null);
        var aggregations = new List<SummaryAggregationCommand> { new(3, "Sum"), new(3, "Avg") };

        var errors = sut.Validate(EmptyInput(groupByFieldId: 1, chart: chart, aggregations: aggregations), fields);

        errors.Should().ContainKey("chart.seriesFieldId");
    }

    [Fact]
    public void Chart_SeriesWithSingleAggregation_IsAccepted()
    {
        var sut = new ChartReportConfigValidator();
        var fields = new List<AppField> { MakeField(1), MakeField(2), MakeField(3, "Number") };
        var chart = new ChartConfigCommand("Bar", 2, "EqualValues", null, null, null, null, false, "Labels", "Asc", null, null, false, false, null);
        var aggregations = new List<SummaryAggregationCommand> { new(3, "Sum") };

        var errors = sut.Validate(EmptyInput(groupByFieldId: 1, chart: chart, aggregations: aggregations), fields);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Gauge_WithoutGaugeFieldId_IsRejected()
    {
        var sut = new ChartReportConfigValidator();
        var fields = new List<AppField> { MakeField(1) };
        var chart = new ChartConfigCommand("Gauge", null, "EqualValues", null, null, null, null, false, "Labels", "Asc", null, null, false, false, null);

        var errors = sut.Validate(EmptyInput(groupByFieldId: 1, chart: chart), fields);

        errors.Should().ContainKey("chart.gaugeFieldId");
    }

    [Fact]
    public void Gauge_FixedGoal_DoesNotRequireGoalField()
    {
        var sut = new ChartReportConfigValidator();
        var fields = new List<AppField> { MakeField(1) };
        var chart = new ChartConfigCommand("Gauge", null, "EqualValues", null, null, null, null, false, "Labels", "Asc", null, null, false, false, null,
            GaugeFieldId: 1, GaugeGoalType: "Fixed");

        var errors = sut.Validate(EmptyInput(groupByFieldId: 1, chart: chart), fields);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Gauge_DataValueGoal_WithoutGoalFieldOrFunction_IsRejected()
    {
        var sut = new ChartReportConfigValidator();
        var fields = new List<AppField> { MakeField(1) };
        var chart = new ChartConfigCommand("Gauge", null, "EqualValues", null, null, null, null, false, "Labels", "Asc", null, null, false, false, null,
            GaugeFieldId: 1, GaugeGoalType: "DataValue");

        var errors = sut.Validate(EmptyInput(groupByFieldId: 1, chart: chart), fields);

        errors.Should().ContainKey("chart.gaugeGoalFieldId");
        errors.Should().ContainKey("chart.gaugeGoalFunction");
    }

    [Fact]
    public void Gauge_DataValueGoal_WithGoalFieldAndFunction_IsAccepted()
    {
        var sut = new ChartReportConfigValidator();
        var fields = new List<AppField> { MakeField(1), MakeField(2, "Number") };
        var chart = new ChartConfigCommand("Gauge", null, "EqualValues", null, null, null, null, false, "Labels", "Asc", null, null, false, false, null,
            GaugeFieldId: 1, GaugeGoalType: "DataValue", GaugeGoalFieldId: 2, GaugeGoalFunction: "Sum");

        var errors = sut.Validate(EmptyInput(groupByFieldId: 1, chart: chart), fields);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void NonGauge_WithGaugeFieldId_IsRejected()
    {
        var sut = new ChartReportConfigValidator();
        var fields = new List<AppField> { MakeField(1) };
        var chart = new ChartConfigCommand("Bar", null, "EqualValues", null, null, null, null, false, "Labels", "Asc", null, null, false, false, null,
            GaugeFieldId: 1);

        var errors = sut.Validate(EmptyInput(groupByFieldId: 1, chart: chart), fields);

        errors.Should().ContainKey("chart.gaugeFieldId");
    }

    [Fact]
    public void Chart_InvalidDataLabelDisplayAs_IsRejected()
    {
        var sut = new ChartReportConfigValidator();
        var fields = new List<AppField> { MakeField(1) };
        var chart = new ChartConfigCommand("Bar", null, "EqualValues", null, null, null, null, false, "Labels", "Asc", null, null, false, false, null,
            DataLabelDisplayAs: "Bogus");

        var errors = sut.Validate(EmptyInput(groupByFieldId: 1, chart: chart), fields);

        errors.Should().ContainKey("chart.dataLabelDisplayAs");
    }

    // ── Registry ─────────────────────────────────────────────────────────────

    [Fact]
    public void Registry_UnknownReportType_ReturnsReportTypeError()
    {
        var registry = new ReportConfigValidatorRegistry([new TableReportConfigValidator(), new SummaryReportConfigValidator(), new ChartReportConfigValidator()]);

        var errors = registry.Validate("GridEdit", EmptyInput(), []);

        errors.Should().ContainKey("ReportType");
    }

    [Fact]
    public void Registry_IsSupported_ReflectsRegisteredTypesOnly()
    {
        var registry = new ReportConfigValidatorRegistry([new TableReportConfigValidator(), new SummaryReportConfigValidator(), new ChartReportConfigValidator()]);

        registry.IsSupported("Table").Should().BeTrue();
        registry.IsSupported("Summary").Should().BeTrue();
        registry.IsSupported("Chart").Should().BeTrue();
        registry.IsSupported("GridEdit").Should().BeFalse();
    }
}
