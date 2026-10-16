namespace PowerBase.Application.Reports.Queries.ExportReport;

/// <param name="RuntimeFilters">Optional extra "fieldId:value" filters ANDed onto the report's own
/// filter — same shape and merge as RunReportQuery's (e.g. an embedded child grid scoping the export
/// to its parent's children).</param>
public record ExportReportQuery(
    Guid ReportPublicId,
    string Format, // Format: "csv" | "xlsx"
    IReadOnlyList<(long FieldId, string Value, string? SubField)>? RuntimeFilters = null);

public class ExportResult
{
    public byte[] Content { get; init; } = [];
    public string ContentType { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
}
