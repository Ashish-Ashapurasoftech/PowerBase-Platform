namespace PowerBase.API.Models.Reports;

public class UpdateReportRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Visibility { get; set; } = "Personal";
    public List<long> Columns { get; set; } = [];
    public long? SortFieldId { get; set; }
    public bool SortDesc { get; set; }
    public List<ReportFilterRequest> Filters { get; set; } = [];
    public long? GroupByFieldId { get; set; }
    public List<SummaryAggregationRequest> Aggregations { get; set; } = [];
}
