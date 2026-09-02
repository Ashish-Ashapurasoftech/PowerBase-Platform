namespace PowerBase.Application.Reports;

public class ReportDefinition
{
    public List<long> Columns { get; set; } = [];

    /// <summary>Table-only: 'Default' (Columns is empty — show all reportable fields) or
    /// 'Custom' (Columns holds the explicit picked set). Purely a UI-reconstruction hint —
    /// RunTableAsync's "empty Columns = all reportable fields" behavior is unchanged and doesn't
    /// read this; it exists so the wizard can round-trip which mode the user explicitly chose.</summary>
    public string ColumnsMode { get; set; } = "Custom";

    // Legacy multi-sort for Summary/Chart (Table reports now use TableSortGroup below — supersedes
    // SortFieldId/SortDesc when non-empty)
    public List<SortSpec> SortFields { get; set; } = [];

    /// <summary>Table-only: one ordered list unifying sort + group (each level is either a plain
    /// sort, or a sort-and-group with a combine mode), replacing the separate GroupByFieldId +
    /// SortFields mechanism for Table reports specifically. Empty = fall back to
    /// GroupByFieldId/SortFields below (legacy-compat, same convention as SortFieldId/SortDesc).
    /// Summary/Chart reports do not use this — they keep GroupByFieldId/SortFields directly.</summary>
    public List<SortGroupLevel> TableSortGroup { get; set; } = [];

    // New filter tree (supersedes Filters when set)
    public FilterGroup? FilterTree { get; set; }

    // Used by Table (single-field Group panel — legacy fallback once TableSortGroup is set) and Summary (Rows).
    public long? GroupByFieldId { get; set; }
    /// <summary>EqualValues (default), FirstWord, FirstLetter</summary>
    public string GroupByMode { get; set; } = "EqualValues";
    public bool HideTotals { get; set; }
    /// <summary>null = "Default report setting" (renders the same as false/Expanded, but keeps
    /// that choice distinguishable from an explicit "Expanded by default" pick), true = Collapsed
    /// by default, false = Expanded by default.</summary>
    public bool? GroupDefaultCollapsed { get; set; }
    public bool GroupByDescending { get; set; }
    public List<SummaryAggregation> Aggregations { get; set; } = [];

    /// <summary>Table-only. Null when not a Table report or when nothing has been customized.</summary>
    public ReportOptions? Options { get; set; }

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

// ── Table: unified Sort + Group ────────────────────────────────────────────────

public class SortGroupLevel
{
    public long FieldId { get; set; }
    public bool Desc { get; set; }
    /// <summary>False = plain sort. True = sort-and-group — GroupByMode applies, and the first
    /// level with IsGroup=true becomes the effective grouping field (matching today's single
    /// GroupByFieldId's role: records of the same group must be contiguous).</summary>
    public bool IsGroup { get; set; }
    /// <summary>Only meaningful when IsGroup is true. EqualValues (default), FirstWord, FirstLetter.</summary>
    public string GroupByMode { get; set; } = "EqualValues";
}

// ── Table: Options ──────────────────────────────────────────────────────────────

public class ReportOptions
{
    /// <summary>Default (no truncation/clamp — today's unstated baseline behavior), Truncate
    /// (single line, ellipsis), Wrap (up to 3 lines).</summary>
    public string ColumnHeaderText { get; set; } = "Default";
    public bool ShowEditIcon { get; set; } = true;
    public bool ShowViewIcon { get; set; } = true;
    /// <summary>Only meaningful when the table has a Quick Peek form configured (Form.
    /// IsQuickPeekForm) — otherwise the icon never shows regardless of this flag.</summary>
    public bool ShowQuickPeekIcon { get; set; } = true;
    public bool DisableBulkDelete { get; set; }
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
    /// <summary>Only meaningful when DataLabelsVisible is true. "Value" (default) or "PercentOfSeries"
    /// (each label shows its share of that dataset's own total). Not applicable to Gauge, which has
    /// no data-labels toggle at all.</summary>
    public string DataLabelDisplayAs { get; set; } = "Value";
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
    /// <summary>"Fixed" (default — GoalValue is a literal number) or "DataValue" (the goal is a
    /// live aggregate — GaugeGoalFieldId summarized by GaugeGoalFunction — resolved at query time).</summary>
    public string GaugeGoalType { get; set; } = "Fixed";
    /// <summary>Only meaningful when GaugeGoalType is "DataValue".</summary>
    public long? GaugeGoalFieldId { get; set; }
    /// <summary>Sum or Avg. Only meaningful when GaugeGoalType is "DataValue".</summary>
    public string? GaugeGoalFunction { get; set; }
}
