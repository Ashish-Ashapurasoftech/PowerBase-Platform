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
    public Guid TableId { get; init; }
    public string TableName { get; init; } = string.Empty;
    public DateTime CreatedOn { get; init; }
    public List<Guid> VisibleToRoleIds { get; init; } = [];
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
    public List<CustomDynamicFilterItemDto> CustomDynamicFilterItems { get; init; } = [];
    public bool AllowQuickSearch { get; init; } = true;

    // Chart-only
    public ChartConfigDto? Chart { get; init; }

    // Legacy fields — included for backward-compat clients
    public long? SortFieldId { get; init; }
    public bool SortDesc { get; init; }
    public List<ReportFilterDto> Filters { get; init; } = [];
}

public class CustomDynamicFilterItemDto
{
    public long FieldId { get; init; }
    public string? SubField { get; init; }
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
    public string? SubField { get; init; }
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

// ── Chart config DTO ─────────────────────────────────────────────────────────

public class ChartConfigDto
{
    public string ChartType { get; init; } = "Bar";
    public long? SeriesFieldId { get; init; }
    public string SeriesMode { get; init; } = "EqualValues";
    public string? AxisLabelX { get; init; }
    public string? AxisLabelY { get; init; }
    public decimal? YMin { get; init; }
    public decimal? YMax { get; init; }
    public bool LogScale { get; init; }
    public string SortBy { get; init; } = "Labels";
    public string SortDirection { get; init; } = "Asc";
    public decimal? GoalValue { get; init; }
    public string? GoalLabel { get; init; }
    public bool DataLabelsVisible { get; init; }
    public bool HideMissingCategories { get; init; }
    public Guid? DrilldownReportId { get; init; }
    public List<long> SecondaryAxisAggregationFieldIds { get; init; } = [];
    public string? AxisLabelY2 { get; init; }
    public decimal? YMin2 { get; init; }
    public decimal? YMax2 { get; init; }
    public bool LogScale2 { get; init; }
    public long? GaugeFieldId { get; init; }
    public decimal GaugeLowMaxPercent { get; init; } = 30;
    public decimal GaugeMediumMaxPercent { get; init; } = 70;
}
