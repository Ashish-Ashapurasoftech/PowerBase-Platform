using PowerBase.Application.Reports;

namespace PowerBase.Application.Relationships.Commands.UpdateReferenceFilter;

/// <summary>Replace a relationship's dependent-dropdown filter: the older <paramref name="Conditions"/> (empty
/// clears them) and the filter tree (null/empty clears it).</summary>
public record UpdateReferenceFilterCommand(
    Guid RelationshipPublicId,
    IReadOnlyList<ReferenceFilterConditionInput> Conditions,
    FilterGroup? FilterTree = null);

/// <summary>One condition as sent by the client: <see cref="JunctionTablePublicId"/> set = junction mode
/// (uses the two junction Fids), otherwise direct mode (uses <see cref="ParentFid"/>).</summary>
public record ReferenceFilterConditionInput(
    int FormFid,
    int? ParentFid,
    Guid? JunctionTablePublicId,
    int? JunctionParentFid,
    int? JunctionValueFid);
