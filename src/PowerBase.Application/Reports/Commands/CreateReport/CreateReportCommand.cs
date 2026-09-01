namespace PowerBase.Application.Reports.Commands.CreateReport;

public record CreateReportCommand(
    Guid TablePublicId,
    string Name,
    string? Description,
    string Visibility,
    string ReportType,
    List<long> Columns,
    List<SortSpec> SortFields,
    FilterGroup? FilterTree,
    long? GroupByFieldId,
    string GroupByMode,
    bool HideTotals,
    bool? GroupDefaultCollapsed,
    bool GroupByDescending,
    List<SummaryAggregationCommand> Aggregations,
    string DynamicFilterType,
    List<long> CustomDynamicFilterFields,
    List<CustomDynamicFilterItem>? CustomDynamicFilterItems,
    bool AllowQuickSearch,
    List<Guid>? VisibleToRoleIds,
    ChartConfigCommand? Chart = null,
    string ColumnsMode = "Custom",
    List<SortGroupLevelCommand>? TableSortGroup = null,
    ReportOptionsCommand? Options = null);

public record SummaryAggregationCommand(long FieldId, string Function, string DisplayAs = "Normal");

public record SortGroupLevelCommand(long FieldId, bool Desc, bool IsGroup, string GroupByMode = "EqualValues");

public record ReportOptionsCommand(
    string ColumnHeaderText = "Default",
    bool ShowEditIcon = true,
    bool ShowViewIcon = true,
    bool ShowQuickPeekIcon = true,
    bool DisableBulkDelete = false);

public record ChartConfigCommand(
    string ChartType,
    long? SeriesFieldId,
    string SeriesMode,
    string? AxisLabelX,
    string? AxisLabelY,
    decimal? YMin,
    decimal? YMax,
    bool LogScale,
    string SortBy,
    string SortDirection,
    decimal? GoalValue,
    string? GoalLabel,
    bool DataLabelsVisible,
    bool HideMissingCategories,
    Guid? DrilldownReportId,
    List<long>? SecondaryAxisAggregationFieldIds = null,
    string? AxisLabelY2 = null,
    decimal? YMin2 = null,
    decimal? YMax2 = null,
    bool LogScale2 = false,
    long? GaugeFieldId = null,
    decimal GaugeLowMaxPercent = 30,
    decimal GaugeMediumMaxPercent = 70,
    string DataLabelDisplayAs = "Value",
    string GaugeGoalType = "Fixed",
    long? GaugeGoalFieldId = null,
    string? GaugeGoalFunction = null);
