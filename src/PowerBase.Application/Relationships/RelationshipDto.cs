namespace PowerBase.Application.Relationships;

/// <summary>API-facing view of a relationship plus its participating fields.</summary>
public sealed class RelationshipDto
{
    public Guid PublicId { get; init; }
    public Guid ParentTablePublicId { get; init; }
    public string ParentTableName { get; init; } = string.Empty;
    public Guid ChildTablePublicId { get; init; }
    public string ChildTableName { get; init; } = string.Empty;
    public int ReferenceFid { get; init; }
    public string ReferenceFieldName { get; init; } = string.Empty;
    public int? ProxyFid { get; init; }
    public IReadOnlyList<RelationshipFieldDto> Fields { get; init; } = [];
}

/// <summary>A field participating in a relationship. <paramref name="Role"/> is "reference", "lookup" or "summary".</summary>
public sealed record RelationshipFieldDto(Guid PublicId, int Fid, string Name, string Role, string TypeCode = "")
{
    /// <summary>"TableName: FieldName" for the source field.
    /// Lookup/proxy → the parent field being pulled down.
    /// ReportLink → the field on this table whose value is matched (null = Record ID#).</summary>
    public string? SourceFieldLabel { get; init; }

    /// <summary>"TableName: FieldName" for the target field.
    /// ReportLink → the reference field in the child table.
    /// Summary → the child field being aggregated (null for Count/Exists).</summary>
    public string? TargetFieldLabel { get; init; }
}
