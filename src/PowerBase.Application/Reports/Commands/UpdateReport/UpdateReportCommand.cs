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
    List<SummaryAggregationCommand> Aggregations,
    string DynamicFilterType,
    List<long> CustomDynamicFilterFields,
    bool AllowQuickSearch);
