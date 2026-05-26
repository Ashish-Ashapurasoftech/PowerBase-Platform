using PowerBase.Application.Reports.Commands.CreateReport;

namespace PowerBase.Application.Reports.Commands.UpdateReport;

public record UpdateReportCommand(
    Guid ReportPublicId,
    string Name,
    string? Description,
    string Visibility,
    List<long> Columns,
    long? SortFieldId,
    bool SortDesc,
    List<ReportFilterCommand> Filters,
    long? GroupByFieldId,
    string GroupByMode,
    bool HideTotals,
    List<SummaryAggregationCommand> Aggregations,
    string DynamicFilterType,
    List<long> CustomDynamicFilterFields,
    bool AllowQuickSearch);
