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
    public List<SummaryAggregation> Aggregations { get; set; } = [];

    // Dynamic Filters
    public string DynamicFilterType { get; set; } = "Default"; // Default, Custom, None
    public List<long> CustomDynamicFilterFields { get; set; } = [];
    public bool AllowQuickSearch { get; set; } = true;

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
