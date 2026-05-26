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
    public DateTime CreatedOn { get; init; }
}

public class ReportDefinitionDto
{
    public List<long> Columns { get; init; } = [];
    public long? SortFieldId { get; init; }
    public bool SortDesc { get; init; }
    public List<ReportFilterDto> Filters { get; init; } = [];
    public long? GroupByFieldId { get; init; }
    public string GroupByMode { get; init; } = "EqualValues";
    public bool HideTotals { get; init; }
    public List<SummaryAggregationDto> Aggregations { get; init; } = [];
    public string DynamicFilterType { get; init; } = "Default";
    public List<long> CustomDynamicFilterFields { get; init; } = [];
    public bool AllowQuickSearch { get; init; } = true;
}

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
