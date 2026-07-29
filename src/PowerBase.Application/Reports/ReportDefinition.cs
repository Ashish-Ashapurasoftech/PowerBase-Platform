namespace PowerBase.Application.Reports;

public class ReportDefinition
{
    public List<long> Columns { get; set; } = [];

    // New multi-sort (supersedes SortFieldId/SortDesc when non-empty)
    public List<SortSpec> SortFields { get; set; } = [];

    // New filter tree (supersedes Filters when set)
    public FilterGroup? FilterTree { get; set; }

    // Summary-only
    public long? GroupByFieldId { get; set; }
    /// <summary>EqualValues (default), FirstWord, FirstLetter</summary>
    public string GroupByMode { get; set; } = "EqualValues";
    public bool HideTotals { get; set; }
    public bool GroupDefaultCollapsed { get; set; }
    public bool GroupByDescending { get; set; }
    public List<SummaryAggregation> Aggregations { get; set; } = [];

    // Dynamic Filters
    public string DynamicFilterType { get; set; } = "Default"; // Default, Custom, None
    public List<long> CustomDynamicFilterFields { get; set; } = [];
    /// <summary>New: structured filter items with optional SubField for Address fields.</summary>
    public List<CustomDynamicFilterItem> CustomDynamicFilterItems { get; set; } = [];
    public bool AllowQuickSearch { get; set; } = true;

    // Chart-only
    public ChartConfig? Chart { get; set; }

    // Legacy compat — kept so old JSON deserializes without data loss
    public long? SortFieldId { get; set; }
    public bool SortDesc { get; set; }
    public List<ReportFilter> Filters { get; set; } = [];
}

// ── Filter tree ──────────────────────────────────────────────────────────────

public class FilterGroup
{
    /// <summary>"and" or "or"</summary>
    public string Logic { get; set; } = "and";
    public List<FilterNode> Nodes { get; set; } = [];
}

/// <summary>Either Condition or Group is set — never both.</summary>
public class FilterNode
{
    public FilterCondition? Condition { get; set; }
    public FilterGroup? Group { get; set; }
}

public class FilterCondition
{
    public long FieldId { get; set; }
    /// <summary>eq, ne, contains, startsWith, gt, gte, lt, lte</summary>
    public string Operator { get; set; } = "eq";
    public string? Value { get; set; }
    /// <summary>Optional JSON sub-field for complex types (e.g. Address). When set, SQL uses JSON_VALUE(col,'$.subfield').</summary>
    public string? SubField { get; set; }
}

// ── Sort ─────────────────────────────────────────────────────────────────────

public class SortSpec
{
    public long FieldId { get; set; }
    public bool Desc { get; set; }
}

// ── Legacy flat filter (kept for backward-compat deserialization) ─────────────

public class ReportFilter
{
    public long FieldId { get; set; }
    /// <summary>eq, ne, contains, startsWith, gt, gte, lt, lte</summary>
    public string Operator { get; set; } = "eq";
    public string? Value { get; set; }
}

// ── Aggregations ─────────────────────────────────────────────────────────────

public class SummaryAggregation
{
    public long FieldId { get; set; }
    /// <summary>Sum, Avg, Min, Max</summary>
    public string Function { get; set; } = "Sum";
    /// <summary>Normal (default) or PercentOfColumnTotal</summary>
    public string DisplayAs { get; set; } = "Normal";
}

// ── Custom Dynamic Filter Item ────────────────────────────────────────────────

/// <summary>
/// A single dynamic filter slot. For Address fields, SubField specifies
/// which JSON property to filter by (city, country, state, zip, street1, street2).
/// </summary>
public class CustomDynamicFilterItem
{
    public long FieldId { get; set; }
    /// <summary>Optional JSON sub-field for Address types (e.g. "city", "country", "zip").</summary>
    public string? SubField { get; set; }
}

// ── Chart config ─────────────────────────────────────────────────────────────

/// <summary>
/// Chart-report-only config. X-axis/category is GroupByFieldId/GroupByMode, and Y-values are
/// Aggregations (both shared with Summary reports) — this class only holds what's unique to charts.
/// </summary>
public class ChartConfig
{
    /// <summary>Bar, StackedBar, HorizontalBar, HorizontalStackedBar, Line, LineBarCombo, Pie, Donut, Gauge, Waterfall, Radial</summary>
    public string ChartType { get; set; } = "Bar";

    /// <summary>Optional second grouping dimension ("Series / Group by") — splits each category into multiple datasets.</summary>
    public long? SeriesFieldId { get; set; }
    /// <summary>EqualValues (default), FirstWord, FirstLetter — same semantics as GroupByMode.</summary>
    public string SeriesMode { get; set; } = "EqualValues";

    public string? AxisLabelX { get; set; }
    public string? AxisLabelY { get; set; }
    public decimal? YMin { get; set; }
    public decimal? YMax { get; set; }
    public bool LogScale { get; set; }

    /// <summary>Labels or Values</summary>
    public string SortBy { get; set; } = "Labels";
    /// <summary>Asc or Desc</summary>
    public string SortDirection { get; set; } = "Asc";

    public decimal? GoalValue { get; set; }
    public string? GoalLabel { get; set; }

    public bool DataLabelsVisible { get; set; }
    public bool HideMissingCategories { get; set; }

    /// <summary>Report opened when a chart segment is clicked, filtered by the clicked category value. Null = use the table's default report.</summary>
    public Guid? DrilldownReportId { get; set; }

    // ── Line / LineBarCombo dual y-axis ──
    /// <summary>Which of the Aggregations field IDs render on the secondary axis (Line) / as bars (LineBarCombo). Everything else renders on the primary axis / as lines.</summary>
    public List<long> SecondaryAxisAggregationFieldIds { get; set; } = [];
    public string? AxisLabelY2 { get; set; }
    public decimal? YMin2 { get; set; }
    public decimal? YMax2 { get; set; }
    public bool LogScale2 { get; set; }

    // ── Gauge ──
    /// <summary>The field the gauge measures. The wizard also uses this as GroupByFieldId under the hood (Gauge has no category axis).</summary>
    public long? GaugeFieldId { get; set; }
    /// <summary>Upper bound (%) of the "Low" color band.</summary>
    public decimal GaugeLowMaxPercent { get; set; } = 30;
    /// <summary>Upper bound (%) of the "Medium" color band; above this is "High".</summary>
    public decimal GaugeMediumMaxPercent { get; set; } = 70;
}
