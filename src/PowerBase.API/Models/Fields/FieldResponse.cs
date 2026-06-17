namespace PowerBase.API.Models.Fields;

public class FieldResponse
{
    public long Id { get; init; }
    public Guid PublicId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Label { get; init; }
    public string? Description { get; init; }
    public string TypeCode { get; init; } = string.Empty;
    public string? PhysicalColumnName { get; init; }
    public string? DefaultValue { get; init; }
    public bool IsRequired { get; init; }
    public bool IsSearchable { get; init; }
    public bool IsSortable { get; init; }
    public bool IsFilterable { get; init; }
    public bool IsReportable { get; init; }
    public bool IsAuditable { get; init; }
    public bool IsUnique { get; init; }
    public bool IsSystem { get; init; }
    public int? Fid { get; init; }
    public string? Settings { get; init; }
    public DateTime CreatedOn { get; init; }
}
