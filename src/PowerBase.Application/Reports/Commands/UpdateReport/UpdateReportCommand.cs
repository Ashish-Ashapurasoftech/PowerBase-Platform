using PowerBase.Application.Reports.Commands.CreateReport;

namespace PowerBase.Application.Reports.Commands.UpdateReport;

public record UpdateReportCommand(
    Guid ReportPublicId,
    string Name,
    string? Description,
    string Visibility,
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
