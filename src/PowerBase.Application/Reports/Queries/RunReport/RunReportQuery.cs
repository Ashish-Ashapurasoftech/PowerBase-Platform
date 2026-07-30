namespace PowerBase.Application.Reports.Queries.RunReport;

public record RunReportQuery(
    Guid ReportPublicId,
    int Page = 1,
    int PageSize = 20,
    IReadOnlyList<(long FieldId, string Value, string? SubField)>? RuntimeFilters = null,
    string? QuickSearch = null,
    /// <summary>Restricts QuickSearch to these field ids instead of every IsSearchable field
    /// on the table. Null/empty means the original "all searchable fields" behavior.</summary>
    IReadOnlyList<long>? QuickSearchFieldIds = null,
    /// <summary>False (default) = "contains"; true = exact match ("eq").</summary>
    bool QuickSearchExact = false);
