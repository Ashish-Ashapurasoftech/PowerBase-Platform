namespace PowerBase.Application.Reports.Queries.ListReportsByTablePaged;

public record ListReportsByTablePagedQuery(
    Guid TablePublicId,
    int Page = 1,
    int PageSize = 20,
    string? Search = null,
    string SortBy = "name",
    bool SortDesc = false);
