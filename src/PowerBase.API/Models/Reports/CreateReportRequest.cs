namespace PowerBase.API.Models.Reports;

public class CreateReportRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Visibility { get; set; } = "Personal";
    public string ReportType { get; set; } = "Table";
    public List<long> Columns { get; set; } = [];
    public List<Guid>? VisibleToRoleIds { get; set; }

    // New multi-sort
    public List<SortSpecRequest> SortFields { get; set; } = [];

    // New filter tree
    public FilterGroupRequest? FilterTree { get; set; }

    // Summary-only
    public long? GroupByFieldId { get; set; }
    public string GroupByMode { get; set; } = "EqualValues";
    public bool HideTotals { get; set; }
    public bool GroupDefaultCollapsed { get; set; }
    public bool GroupByDescending { get; set; }
    public List<SummaryAggregationRequest> Aggregations { get; set; } = [];
    public string DynamicFilterType { get; set; } = "Default";
    public List<long> CustomDynamicFilterFields { get; set; } = [];
    public bool AllowQuickSearch { get; set; } = true;
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
