namespace PowerBase.API.Models.Reports;

public class CreateReportRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Visibility { get; set; } = "Personal";
    /// <summary>Table or Summary (M1 only — Chart/Kanban/Map etc. are out of scope)</summary>
    public string ReportType { get; set; } = "Table";
    public List<long> Columns { get; set; } = [];
    public long? SortFieldId { get; set; }
    public bool SortDesc { get; set; }
    public List<ReportFilterRequest> Filters { get; set; } = [];
    // Summary-only
    public long? GroupByFieldId { get; set; }
    public List<SummaryAggregationRequest> Aggregations { get; set; } = [];
}

public class ReportFilterRequest
{
    public long FieldId { get; set; }
    public string Operator { get; set; } = "eq";
    public string? Value { get; set; }
}

public class SummaryAggregationRequest
{
    public long FieldId { get; set; }
    public string Function { get; set; } = "Count";
}
