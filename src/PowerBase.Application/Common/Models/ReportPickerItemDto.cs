namespace PowerBase.Application.Common.Models;

/// <summary>Slim report row for the form designer's "Choose a report" dropdown. Unlike the
/// panel/grid lists it includes Hidden reports, and carries the relationship that auto-created
/// the report (null for ordinary reports) so the designer can preselect it.</summary>
public class ReportPickerItemDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string ReportType { get; init; } = string.Empty;
    public string Visibility { get; init; } = string.Empty;
    public Guid? RelationshipId { get; init; }
}
