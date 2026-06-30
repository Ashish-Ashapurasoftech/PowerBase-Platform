namespace PowerBase.Application.Relationships.Commands.RemoveRelationshipField;

/// <summary>Removes a single Lookup or Summary field from a relationship. The reference field
/// cannot be removed this way (delete the whole relationship instead).</summary>
public record RemoveRelationshipFieldCommand(Guid RelationshipPublicId, Guid FieldPublicId);
