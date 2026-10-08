namespace PowerBase.API.Models.Reports;

/// <summary>Form-designer report picker row (includes Hidden reports). RelationshipId is set for
/// the report auto-created for a relationship.</summary>
public class ReportPickerItemResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string ReportType { get; init; } = string.Empty;
    public string Visibility { get; init; } = string.Empty;
    public Guid? RelationshipId { get; init; }
}
