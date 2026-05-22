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
    List<SummaryAggregationCommand> Aggregations);
