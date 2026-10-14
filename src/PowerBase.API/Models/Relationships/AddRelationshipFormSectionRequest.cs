namespace PowerBase.API.Models.Relationships;

/// <summary>Adds the relationship's "{Child} details" section to its parent forms.</summary>
public class AddRelationshipFormSectionRequest
{
    /// <summary>The hidden "Embedded for {Parent}" report created for the relationship.</summary>
    public Guid ReportPublicId { get; set; }
    public string ReportName { get; set; } = string.Empty;
}
