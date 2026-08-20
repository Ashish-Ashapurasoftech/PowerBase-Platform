namespace PowerBase.Application.Common.Models;

/// <summary>Slim projection of a report for the paged reports grid — excludes Definition,
/// DisplayOrder, ViewEditFormId, and other detail only needed by GET /reports/{publicId}.</summary>
public class ReportListItemDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string ReportType { get; set; } = string.Empty;
    public string Visibility { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public DateTime CreatedOn { get; set; }
}
