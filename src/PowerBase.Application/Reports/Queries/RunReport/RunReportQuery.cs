namespace PowerBase.Application.Reports.Queries.RunReport;

public record RunReportQuery(Guid ReportPublicId, int Page = 1, int PageSize = 20);
