namespace PowerBase.Application.Relationships.Commands.DeleteRelationship;

/// <summary>Deletes a relationship and its lookup/summary fields. Blocked when the
/// reference column already holds data unless <paramref name="Force"/> is set. A reference converted from an existing
/// field is put back as a real field holding the parent's key text; one created with the relationship is kept as a field
/// of the parent key's type (a Date key gives a Date field) holding the key values (see <see cref="OriginalFieldRestore"/>).</summary>
public record DeleteRelationshipCommand(Guid RelationshipPublicId, bool Force, OriginalFieldRestore Restore = OriginalFieldRestore.Strict);

/// <summary>What deleting a relationship does with a reference field that was converted from an existing field. A reference
/// created with the relationship is rebuilt as the parent key's type unless this is None (then it is deleted); if that
/// can't be done it is deleted, as it always was, rather than refused.</summary>
public enum OriginalFieldRestore
{
    /// <summary>Don't rebuild the field's data — just revert it to a plain Number (used when its table is being deleted anyway).</summary>
    None,
    /// <summary>Rebuild it; if that can't be done, fall back to the plain-Number revert (which loses the text).</summary>
    BestEffort,
    /// <summary>Rebuild it; if that can't be done, refuse the delete (nothing changed) rather than lose the text. The default.</summary>
    Strict,
}
