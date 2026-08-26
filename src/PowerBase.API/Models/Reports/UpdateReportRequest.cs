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
