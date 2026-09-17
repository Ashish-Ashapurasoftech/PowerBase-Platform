namespace PowerBase.Application.Relationships.Commands.UpdateDisplayKey;

/// <summary>Change one relationship's own display key override. Scoped to this relationship only —
/// other relationships pointing at the same parent table are unaffected.</summary>
/// <param name="DisplayKeyFieldFid">Parent field Fid to use as the picker/grid/filter label; 3 or null
/// reverts to Standard key (Record ID# / the parent table's global KeyFieldId).</param>
public record UpdateDisplayKeyCommand(Guid RelationshipPublicId, int? DisplayKeyFieldFid);
