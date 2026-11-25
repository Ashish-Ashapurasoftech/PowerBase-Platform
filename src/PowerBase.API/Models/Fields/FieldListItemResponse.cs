namespace PowerBase.API.Models.Fields;

/// <summary>Slim shape for the fields grid listing endpoint — full details (Settings,
/// PhysicalColumnName, DefaultValue, etc.) are not needed there.</summary>
public class FieldListItemResponse
{
    public long Id { get; init; }
    public Guid PublicId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Label { get; init; }
    public string? Description { get; init; }
    public string TypeCode { get; init; } = string.Empty;
    /// <summary>Reference, Lookup and Summary only: the type the field is shown as (its settings' resultTypeCode), so the
    /// grid can name it "Currency (Lookup)" without the settings. Null for other fields and for one that saved no type.</summary>
    public string? ResultTypeCode { get; init; }
    public bool IsRequired { get; init; }
    public bool IsSearchable { get; init; }
    public bool IsSortable { get; init; }
    public bool IsFilterable { get; init; }
    public bool IsReportable { get; init; }
    public bool IsAuditable { get; init; }
    public bool IsUnique { get; init; }
    public bool IsSystem { get; init; }
    public bool IsAutoFill { get; init; }
    /// <summary>Snapshot field: Fid of the source field captured once at record creation; null when this is not a snapshot field.</summary>
    public int? SnapshotSourceFid { get; init; }
    public bool SnapshotEditable { get; init; }
    public bool IsSharedValues { get; init; }
    public int? Fid { get; init; }
    public DateTime CreatedOn { get; init; }
    /// <summary>Whether this is the table's current key field (Set Key feature).</summary>
    public bool IsKeyField { get; init; }
}
