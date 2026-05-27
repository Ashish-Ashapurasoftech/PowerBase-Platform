namespace PowerBase.Application.Reports.Queries.ExportReport;

public record ExportReportQuery(Guid ReportPublicId, string Format); // Format: "csv" | "xlsx"

public class ExportResult
{
    public byte[] Content { get; init; } = [];
    public string ContentType { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
}
