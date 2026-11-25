namespace PowerBase.Application.Reports.Queries.ListReportsByTablePaged;

public record ListReportsByTablePagedQuery(
    Guid TablePublicId,
    int Page = 1,
    int PageSize = 20,
    string? Search = null,
    string SortBy = "name",
    bool SortDesc = false,
    /// <summary>The Table Settings reports list also lists Hidden reports (they are unlisted everywhere else).</summary>
    bool IncludeHidden = false);
