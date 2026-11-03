namespace PowerBase.Application.Relationships.Commands.UpdateParentLink;

/// <summary>Turn "Display as a link to the parent record" on or off for a relationship's Reference field or one of
/// its Lookup fields (Quickbase's DisplayAsLinkToParent).</summary>
public record UpdateParentLinkCommand(Guid RelationshipPublicId, Guid FieldPublicId, bool DisplayAsLinkToParent);
