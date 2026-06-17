namespace PowerBase.Application.Relationships;

/// <summary>One selectable parent record for a Reference field picker: the parent row Id
/// (stored in the child's reference column) and its display label (the parent's display field).</summary>
public sealed record ReferenceOption(long Id, string? Label);
