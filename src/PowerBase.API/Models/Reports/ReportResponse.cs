namespace PowerBase.API.Models.Reports;

public class ReportResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string ReportType { get; init; } = "Table";
    public string Visibility { get; init; } = "Personal";
    public ReportDefinitionDto Definition { get; init; } = new();
    public bool IsDefault { get; init; }
    public int DisplayOrder { get; init; }
    public Guid? ViewEditFormId { get; init; }
    public DateTime CreatedOn { get; init; }
}

public class ReportDefinitionDto
{
    public List<long> Columns { get; init; } = [];

    // New multi-sort
    public List<SortSpecDto> SortFields { get; init; } = [];

    // New filter tree
    public FilterGroupDto? FilterTree { get; init; }

    public long? GroupByFieldId { get; init; }
    public string GroupByMode { get; init; } = "EqualValues";
    public bool HideTotals { get; init; }
    public bool GroupDefaultCollapsed { get; init; }
    public bool GroupByDescending { get; init; }
    public List<SummaryAggregationDto> Aggregations { get; init; } = [];
    public string DynamicFilterType { get; init; } = "Default";
    public List<long> CustomDynamicFilterFields { get; init; } = [];
    public bool AllowQuickSearch { get; init; } = true;

    // Legacy fields — included for backward-compat clients
    public long? SortFieldId { get; init; }
    public bool SortDesc { get; init; }
    public List<ReportFilterDto> Filters { get; init; } = [];
}

// ── Filter tree DTOs ──────────────────────────────────────────────────────────

public class FilterGroupDto
{
    public string Logic { get; init; } = "and";
    public List<FilterNodeDto> Nodes { get; init; } = [];
}

public class FilterNodeDto
{
    public FilterConditionDto? Condition { get; init; }
    public FilterGroupDto? Group { get; init; }
}

public class FilterConditionDto
{
    public long FieldId { get; init; }
    public string Operator { get; init; } = "eq";
    public string? Value { get; init; }
}

// ── Sort DTOs ─────────────────────────────────────────────────────────────────

public class SortSpecDto
{
    public long FieldId { get; init; }
    public bool Desc { get; init; }
}

// ── Legacy flat filter DTO ────────────────────────────────────────────────────

public class ReportFilterDto
{
    public long FieldId { get; init; }
    public string Operator { get; init; } = "eq";
    public string? Value { get; init; }
}

public class SummaryAggregationDto
{
    public long FieldId { get; init; }
    public string Function { get; init; } = "Sum";
    public string DisplayAs { get; init; } = "Normal";
}
