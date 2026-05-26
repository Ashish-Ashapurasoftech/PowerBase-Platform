namespace PowerBase.Application.Reports.Commands.CreateReport;

public record CreateReportCommand(
    Guid TablePublicId,
    string Name,
    string? Description,
    string Visibility,
    string ReportType,
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

public record ReportFilterCommand(long FieldId, string Operator, string? Value);
public record SummaryAggregationCommand(long FieldId, string Function, string DisplayAs = "Normal");
