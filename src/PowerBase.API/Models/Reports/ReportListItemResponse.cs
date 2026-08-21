namespace PowerBase.API.Models.Reports;

/// <summary>Slim shape for the paged reports grid — full detail (Definition, ViewEditFormId, etc.)
/// is available via GET /reports/{publicId}.</summary>
public class ReportListItemResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string ReportType { get; init; } = string.Empty;
    public string Visibility { get; init; } = string.Empty;
    public bool IsDefault { get; init; }
    public DateTime CreatedOn { get; init; }
}
