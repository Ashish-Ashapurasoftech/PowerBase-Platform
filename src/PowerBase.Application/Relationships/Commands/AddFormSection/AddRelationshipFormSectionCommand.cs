namespace PowerBase.Application.Relationships.Commands.AddFormSection;

/// <summary>Adds a "{Child} details" section to the relationship's parent-table forms that auto-add new
/// fields. It holds a Report element pointing at the relationship's embedded report (filtered through the
/// relationship's Report Link field) followed by the "Add {Child}" link field.</summary>
/// <param name="RelationshipPublicId">The relationship.</param>
/// <param name="ReportPublicId">The hidden "Embedded for {Parent}" report created for the relationship.</param>
/// <param name="ReportName">Display name cached on the Report element.</param>
public record AddRelationshipFormSectionCommand(Guid RelationshipPublicId, Guid ReportPublicId, string ReportName);
