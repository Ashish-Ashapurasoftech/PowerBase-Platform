namespace PowerBase.Application.Reports.Queries.RunReport;

public record RunReportQuery(
    Guid ReportPublicId,
    int Page = 1,
    int PageSize = 20,
    IReadOnlyList<(long FieldId, string Value)>? RuntimeFilters = null,
    string? QuickSearch = null);
