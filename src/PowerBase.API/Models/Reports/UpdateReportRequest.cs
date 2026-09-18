namespace PowerBase.API.Models.Reports;

public class UpdateReportRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Visibility { get; set; } = "Personal";
    public List<long> Columns { get; set; } = [];
    public string ColumnsMode { get; set; } = "Custom";
    public List<Guid>? VisibleToRoleIds { get; set; }

    // New multi-sort (Summary/Chart)
    public List<SortSpecRequest> SortFields { get; set; } = [];

    public List<SortGroupLevelRequest> TableSortGroup { get; set; } = [];

    // New filter tree
    public FilterGroupRequest? FilterTree { get; set; }

    // Table (Group panel) / Summary (Rows)
    public long? GroupByFieldId { get; set; }
    public string GroupByMode { get; set; } = "EqualValues";
    /// <summary>Summary-only: ordered "Rows" group levels — supersedes GroupByFieldId/GroupByMode
    /// when non-empty.</summary>
    public List<RowGroupLevelRequest> RowGroupLevels { get; set; } = [];
    /// <summary>Summary-only, and only when no crosstab column is configured — the report's
    /// default row order, referencing its own output columns (a Rows level, Count, or an
    /// aggregation) rather than a raw table field.</summary>
    public List<SummarySortFieldRequest> SummarySortFields { get; set; } = [];
    public bool HideTotals { get; set; }
    public bool? GroupDefaultCollapsed { get; set; }
    public bool GroupByDescending { get; set; }
    public List<SummaryAggregationRequest> Aggregations { get; set; } = [];
    public string DynamicFilterType { get; set; } = "Default";
    public List<long> CustomDynamicFilterFields { get; set; } = [];
    public List<CustomDynamicFilterItemRequest> CustomDynamicFilterItems { get; set; } = [];
    public bool AllowQuickSearch { get; set; } = true;

    // Chart-only
    public ChartConfigRequest? Chart { get; set; }

    public ReportOptionsRequest? Options { get; set; }
}
