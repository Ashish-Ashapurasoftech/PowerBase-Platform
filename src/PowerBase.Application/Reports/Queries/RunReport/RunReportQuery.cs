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
    bool ClearGrouping = false,
    /// <summary>Answers to this report's saved "&lt;ask the user&gt;" filter conditions, not
    /// persisted — keyed by tree-path (see RunReportRequest.AskAnswers), substituted directly
    /// into the saved FilterTree in place before it's resolved, preserving its exact AND/OR
    /// structure.</summary>
    IReadOnlyDictionary<string, string>? AskAnswers = null,
    /// <summary>Summary reports (without "Group columns") only: return just this Page/PageSize slice of
    /// the groups instead of every group, with TotalCount = number of groups and grand totals worked out
    /// over every group (PagedReportRunResult.SummaryTotals). Opt-in so dashboards, the Quickbase API and
    /// other callers that expect every group keep getting them.</summary>
    bool PageSummary = false,
    /// <summary>With PageSummary: ad-hoc sort by a Summary column's Key (e.g. "agg0_5", "0"), applied to
    /// every group before the page is cut, so paging and sorting agree.</summary>
    string? SummarySortKey = null,
    bool SummarySortDesc = false,
    /// <summary>The caller already has the match count for this exact question (only the page changed): skip the COUNT query and
    /// answer TotalCount = -1 (keep yours).</summary>
    bool SkipCount = false,
    /// <summary>The caller already has the column totals for this exact question (only the page changed): skip TotalsOverAllMatchesAsync
    /// and answer TotalsSkipped = true (keep yours).</summary>
    bool SkipTotals = false);
