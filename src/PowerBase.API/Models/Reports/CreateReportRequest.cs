namespace PowerBase.API.Models.Reports;

public class CreateReportRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Visibility { get; set; } = "Personal";
    public string ReportType { get; set; } = "Table";
    public List<long> Columns { get; set; } = [];
    /// <summary>Table-only. "Default" (Columns empty — all reportable fields) or "Custom".</summary>
    public string ColumnsMode { get; set; } = "Custom";
    public List<Guid>? VisibleToRoleIds { get; set; }

    // New multi-sort (Summary/Chart)
    public List<SortSpecRequest> SortFields { get; set; } = [];

    /// <summary>Table-only unified Sort + Group list.</summary>
    public List<SortGroupLevelRequest> TableSortGroup { get; set; } = [];

    // New filter tree
    public FilterGroupRequest? FilterTree { get; set; }

    // Table (Group panel) / Summary (Rows)
    public long? GroupByFieldId { get; set; }
    public string GroupByMode { get; set; } = "EqualValues";
    public bool HideTotals { get; set; }
    /// <summary>null = "Default report setting", true = Collapsed by default, false = Expanded by default.</summary>
    public bool? GroupDefaultCollapsed { get; set; }
    public bool GroupByDescending { get; set; }
    public List<SummaryAggregationRequest> Aggregations { get; set; } = [];
    public string DynamicFilterType { get; set; } = "Default";
    public List<long> CustomDynamicFilterFields { get; set; } = [];
    public List<CustomDynamicFilterItemRequest> CustomDynamicFilterItems { get; set; } = [];
    public bool AllowQuickSearch { get; set; } = true;

    // Chart-only
    public ChartConfigRequest? Chart { get; set; }

    /// <summary>Table-only.</summary>
    public ReportOptionsRequest? Options { get; set; }
}

public class SortGroupLevelRequest
{
    public long FieldId { get; set; }
    public bool Desc { get; set; }
    public bool IsGroup { get; set; }
    public string GroupByMode { get; set; } = "EqualValues";
}

public class ReportOptionsRequest
{
    public string ColumnHeaderText { get; set; } = "Default";
    public bool ShowEditIcon { get; set; } = true;
    public bool ShowViewIcon { get; set; } = true;
    public bool DisableBulkDelete { get; set; }
}

public class CustomDynamicFilterItemRequest
{
    public long FieldId { get; set; }
    public string? SubField { get; set; }
}

// ── Chart config request model ────────────────────────────────────────────────

public class ChartConfigRequest
{
    public string ChartType { get; set; } = "Bar";
    public long? SeriesFieldId { get; set; }
    public string SeriesMode { get; set; } = "EqualValues";
    public string? AxisLabelX { get; set; }
    public string? AxisLabelY { get; set; }
    public decimal? YMin { get; set; }
    public decimal? YMax { get; set; }
    public bool LogScale { get; set; }
    public string SortBy { get; set; } = "Labels";
    public string SortDirection { get; set; } = "Asc";
    public decimal? GoalValue { get; set; }
    public string? GoalLabel { get; set; }
    public bool DataLabelsVisible { get; set; }
    public bool HideMissingCategories { get; set; }
    public Guid? DrilldownReportId { get; set; }
    public List<long> SecondaryAxisAggregationFieldIds { get; set; } = [];
    public string? AxisLabelY2 { get; set; }
    public decimal? YMin2 { get; set; }
    public decimal? YMax2 { get; set; }
    public bool LogScale2 { get; set; }
    public long? GaugeFieldId { get; set; }
    public decimal GaugeLowMaxPercent { get; set; } = 30;
    public decimal GaugeMediumMaxPercent { get; set; } = 70;
    public string DataLabelDisplayAs { get; set; } = "Value";
    public string GaugeGoalType { get; set; } = "Fixed";
    public long? GaugeGoalFieldId { get; set; }
    public string? GaugeGoalFunction { get; set; }
}

// ── Filter tree request models ────────────────────────────────────────────────

public class FilterGroupRequest
{
    public string Logic { get; set; } = "and";
    public List<FilterNodeRequest> Nodes { get; set; } = [];
}

public class FilterNodeRequest
{
    public FilterConditionRequest? Condition { get; set; }
    public FilterGroupRequest? Group { get; set; }
}

public class FilterConditionRequest
{
    public long FieldId { get; set; }
    public string Operator { get; set; } = "eq";
    public string? Value { get; set; }
    /// <summary>Optional JSON sub-field for complex types (e.g. Address street/city, DateRange start/end).</summary>
    public string? SubField { get; set; }
}

// ── Sort request model ────────────────────────────────────────────────────────

public class SortSpecRequest
{
    public long FieldId { get; set; }
    public bool Desc { get; set; }
}

// ── Aggregation request model ─────────────────────────────────────────────────

public class SummaryAggregationRequest
{
    public long FieldId { get; set; }
    public string Function { get; set; } = "Sum";
    public string DisplayAs { get; set; } = "Normal";
}
