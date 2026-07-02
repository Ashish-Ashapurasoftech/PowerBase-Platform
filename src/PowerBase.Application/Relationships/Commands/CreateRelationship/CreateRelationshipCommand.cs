namespace PowerBase.Application.Relationships.Commands.CreateRelationship;

/// <summary>
/// Creates a one-to-many relationship: provisions the Reference field on the child table,
/// any chosen Lookup fields on the child, any chosen Summary fields on the parent, and the
/// meta.Relationship row linking them. The first lookup becomes the reference proxy.
/// </summary>
public record CreateRelationshipCommand(
    Guid AppPublicId,
    Guid ParentTablePublicId,
    Guid ChildTablePublicId,
    string ReferenceFieldName,
    string? ReferenceFieldLabel,
    bool IsReferenceRequired,
    IReadOnlyList<CreateLookupSpec> Lookups,
    IReadOnlyList<CreateSummarySpec> Summaries,
    int? ReferenceFieldFid = null);

/// <summary>A parent field to pull down onto the child as a Lookup. <paramref name="SourceFid"/> is the parent field's Fid.</summary>
public record CreateLookupSpec(int SourceFid, string Name, string? Label);

/// <summary>A child aggregate to roll up onto the parent as a Summary. <paramref name="TargetFid"/> is null for Count.</summary>
public record CreateSummarySpec(string Name, string? Label, string Function, int? TargetFid);
