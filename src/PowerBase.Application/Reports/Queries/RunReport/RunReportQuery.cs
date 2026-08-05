using PowerBase.Application.Reports;

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
    bool QuickSearchExact = false,
    /// <summary>Ad-hoc nested filter tree (Advanced builder / per-column filters), not persisted
    /// to the report — AND'd on top of the saved FilterTree + role ViewFilter.</summary>
    FilterGroup? RuntimeFilterTree = null,
    /// <summary>Ad-hoc single-column sort, not persisted — replaces the report's saved sort when set.</summary>
    long? RuntimeSortFieldId = null,
    bool RuntimeSortDesc = false,
    /// <summary>Ad-hoc grouping, not persisted — overrides the report's saved GroupByFieldId for Table reports.</summary>
    long? RuntimeGroupByFieldId = null,
    bool RuntimeGroupByDesc = false,
    /// <summary>Explicitly clears grouping for this run even if the saved report has GroupByFieldId set.</summary>
    bool ClearGrouping = false);
