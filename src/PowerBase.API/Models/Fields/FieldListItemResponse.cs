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
    public bool IsRequired { get; init; }
    public bool IsSearchable { get; init; }
    public bool IsSortable { get; init; }
    public bool IsFilterable { get; init; }
    public bool IsReportable { get; init; }
    public bool IsAuditable { get; init; }
    public bool IsUnique { get; init; }
    public bool IsSystem { get; init; }
    public int? Fid { get; init; }
    public DateTime CreatedOn { get; init; }
    /// <summary>Whether this is the table's current key field (Set Key feature).</summary>
    public bool IsKeyField { get; init; }
}
