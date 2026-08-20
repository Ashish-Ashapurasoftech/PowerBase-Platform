namespace PowerBase.Application.Relationships.Commands.AddLookupFields;

/// <summary>Adds one or more Lookup fields to an existing relationship's child table.</summary>
public record AddLookupFieldsCommand(Guid RelationshipPublicId, IReadOnlyList<AddLookupSpec> Lookups);

/// <summary>A parent field to pull down as a Lookup. <paramref name="SourceFid"/> is the parent field's Fid.</summary>
public record AddLookupSpec(int SourceFid, string Label);
