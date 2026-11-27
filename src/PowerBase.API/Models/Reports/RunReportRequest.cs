namespace PowerBase.API.Models.Reports;

/// <summary>
/// Body for POST /reports/{publicId}/run — the ad-hoc (not persisted) counterpart to GET run's
/// query-string params. A POST body is required rather than more query params because the
/// Advanced filter builder's nested FilterTree can be arbitrarily deep and would risk hitting
/// URL length limits as a query string.
/// </summary>
public class RunReportRequest
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public List<string>? DynamicFilters { get; set; }
    public string? QuickSearch { get; set; }
    public List<long>? SearchFieldIds { get; set; }
    public bool ExactMatch { get; set; }

    /// <summary>Ad-hoc nested filter tree (Advanced builder / per-column filters) — AND'd on top
    /// of the report's saved filter tree and role ViewFilter, never persisted.</summary>
    public FilterGroupRequest? FilterTree { get; set; }

    /// <summary>Ad-hoc single-column sort — replaces the report's saved sort when set.</summary>
    public long? SortFieldId { get; set; }
    public bool SortDesc { get; set; }

    /// <summary>Ad-hoc grouping (per-column kebab menu) — overrides the report's saved GroupByFieldId.</summary>
    public long? GroupByFieldId { get; set; }
    public bool GroupByDesc { get; set; }
    /// <summary>Explicitly clears grouping for this run even if the saved report has one.</summary>
    public bool ClearGrouping { get; set; }

    /// <summary>Answers to this report's saved "&lt;ask the user&gt;" filter conditions, never
    /// persisted. Keyed by the condition's tree-path (root-to-leaf node indices, joined with "-"
    /// — see table-report-view.component.ts's askConditionKey/collectAskConditions), NOT by
    /// fieldId, since two ask-conditions can target the same field with different operators (a
    /// date range). Substituted directly into the saved filter tree in place, preserving its
    /// exact AND/OR structure — this is NOT merged as a separate AND'd-on-top group, because a
    /// flat AND merge can't correctly represent an ask-condition that lives inside an OR group.</summary>
    public Dictionary<string, string>? AskAnswers { get; set; }

    /// <summary>Summary reports only: page the groups (Page/PageSize) like a Table report instead of
    /// returning every group at once. See RunReportQuery.PageSummary.</summary>
    public bool PageSummary { get; set; }
    /// <summary>With PageSummary: sort the groups by this Summary column Key before paging.</summary>
    public string? SummarySortKey { get; set; }
    public bool SummarySortDesc { get; set; }

    /// <summary>Only the page changed since the last run with the same filters and sort: the client keeps its count, the server skips the
    /// COUNT query and answers totalCount = -1.</summary>
    public bool SkipCount { get; set; }

    /// <summary>Only the page changed since the last run with the same filters and sort: the client keeps its column totals, the server
    /// skips the Totals aggregate query and answers totalsSkipped = true.</summary>
    public bool SkipTotals { get; set; }
}
