namespace PowerBase.Application.Relationships.Commands.UpdateReferenceFilter;

/// <summary>Replace a relationship's dependent-dropdown conditions (empty list clears them).</summary>
public record UpdateReferenceFilterCommand(Guid RelationshipPublicId, IReadOnlyList<ReferenceFilterConditionInput> Conditions);

/// <summary>One condition as sent by the client: <see cref="JunctionTablePublicId"/> set = junction mode
/// (uses the two junction Fids), otherwise direct mode (uses <see cref="ParentFid"/>).</summary>
public record ReferenceFilterConditionInput(
    int FormFid,
    int? ParentFid,
    Guid? JunctionTablePublicId,
    int? JunctionParentFid,
    int? JunctionValueFid);
