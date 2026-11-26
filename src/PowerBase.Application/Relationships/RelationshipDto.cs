namespace PowerBase.Application.Relationships;

/// <summary>API-facing view of a relationship plus its participating fields.</summary>
public sealed class RelationshipDto
{
    public Guid PublicId { get; init; }
    public Guid ParentTablePublicId { get; init; }
    public string ParentTableName { get; init; } = string.Empty;
    public Guid ChildTablePublicId { get; init; }
    public string ChildTableName { get; init; } = string.Empty;
    /// <summary>What one record of each table is called ("Department", "Employee"), for naming the relationship the
    /// Quickbase way: "Department ⇉ Many Employee". Falls back to the table name when the table has no singular label.</summary>
    public string ParentSingularLabel { get; init; } = string.Empty;
    public string ChildSingularLabel { get; init; } = string.Empty;
    public int ReferenceFid { get; init; }
    public string ReferenceFieldName { get; init; } = string.Empty;
    /// <summary>Fid of the reference proxy (a lookup of this relationship or a formula field on the child table), or
    /// null when there is none. Mirrors <see cref="Relationship.ProxyFieldId"/>.</summary>
    public int? ProxyFid { get; init; }
    /// <summary>Label of the reference proxy field, or null. A formula proxy is not one of <see cref="Fields"/>
    /// (it isn't a relationship field), so this is how the client names it.</summary>
    public string? ProxyFieldName { get; init; }
    /// <summary>
    /// Fid of the parent field used as the display key in the reference picker, or null for the
    /// standard key (Record ID# / table global KeyFieldId). Mirrors <see cref="Relationship.DisplayKeyFieldId"/>.
    /// </summary>
    public int? DisplayKeyFid { get; init; }
    public IReadOnlyList<RelationshipFieldDto> Fields { get; init; } = [];
    /// <summary>Non-fatal notes for the caller, e.g. rows whose value had no parent match and were kept in a hidden "original" field.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>A field participating in a relationship. <paramref name="Role"/> is "reference", "key", "proxy", "lookup",
/// "summary", "reportlink" or "addrecord" (the parent's auto "Add {child}" Formula URL).</summary>
public sealed record RelationshipFieldDto(Guid PublicId, int Fid, string Name, string Role, string TypeCode = "")
{
    /// <summary>"TableName: FieldName" for the source field.
    /// Lookup/proxy â the parent field being pulled down.
    /// ReportLink â the field on this table whose value is matched (null = Record ID#).</summary>
    public string? SourceFieldLabel { get; init; }

    /// <summary>"TableName: FieldName" for the target field.
    /// ReportLink â the reference field in the child table.
    /// Summary â the child field being aggregated (null for Count/Exists).</summary>
    public string? TargetFieldLabel { get; init; }

    public Guid? SourceFieldPublicId { get; init; }
    public Guid? TargetFieldPublicId { get; init; }

    /// <summary>Reference, lookup / proxy and summary only: the type the field is shown as (its settings' resultTypeCode —
    /// "Currency" for a lookup of a Currency field), so the screen can name it "Currency (Lookup)". Null when not saved.</summary>
    public string? ResultTypeCode { get; init; }
}
