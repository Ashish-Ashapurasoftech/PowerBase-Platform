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
}
