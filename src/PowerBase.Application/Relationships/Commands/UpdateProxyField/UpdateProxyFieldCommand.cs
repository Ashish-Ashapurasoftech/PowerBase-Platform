namespace PowerBase.Application.Relationships.Commands.UpdateProxyField;

/// <summary>Choose the child field that stands in for the relationship's Reference on forms and reports
/// (Quickbase's "Proxy field").</summary>
/// <param name="ProxyFid">Fid of a Lookup of this relationship or a Formula field on the child table;
/// null clears the proxy.</param>
public record UpdateProxyFieldCommand(Guid RelationshipPublicId, int? ProxyFid);
