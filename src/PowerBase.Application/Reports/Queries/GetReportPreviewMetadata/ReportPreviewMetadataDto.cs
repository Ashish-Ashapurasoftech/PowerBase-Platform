using PowerBase.Application.Reports.Queries.RunReport;

namespace PowerBase.Application.Reports.Queries.GetReportPreviewMetadata;

public class ReportPreviewMetadataDto
{
    public Guid ReportId { get; set; }
    public string ReportName { get; set; } = string.Empty;
    public string ReportType { get; set; } = string.Empty;
    public Guid TableId { get; set; }
    public string TableName { get; set; } = string.Empty;
    public int TotalCount { get; set; }
    public IReadOnlyList<ReportColumnInfo> Columns { get; set; } = Array.Empty<ReportColumnInfo>();
    public IReadOnlyList<ReportAggregationPreviewDto> Aggregations { get; set; } = Array.Empty<ReportAggregationPreviewDto>();
    public bool IsDataMasked { get; set; } = true;
}

public class ReportAggregationPreviewDto
{
    public long FieldId { get; set; }
    public string Function { get; set; } = string.Empty;
    public string? DisplayAs { get; set; }
    public string? Label { get; set; }
}
