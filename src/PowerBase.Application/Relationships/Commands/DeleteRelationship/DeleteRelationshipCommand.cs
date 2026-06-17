namespace PowerBase.Application.Relationships.Commands.DeleteRelationship;

/// <summary>Deletes a relationship and its reference/lookup/summary fields. Blocked when the
/// reference column already holds data unless <paramref name="Force"/> is set.</summary>
public record DeleteRelationshipCommand(Guid RelationshipPublicId, bool Force);
