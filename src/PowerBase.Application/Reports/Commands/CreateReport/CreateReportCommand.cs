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
    bool GroupDefaultCollapsed,
    bool GroupByDescending,
    List<SummaryAggregationCommand> Aggregations,
    string DynamicFilterType,
    List<long> CustomDynamicFilterFields,
    List<CustomDynamicFilterItem>? CustomDynamicFilterItems,
    bool AllowQuickSearch,
    List<Guid>? VisibleToRoleIds);

public record SummaryAggregationCommand(long FieldId, string Function, string DisplayAs = "Normal");
