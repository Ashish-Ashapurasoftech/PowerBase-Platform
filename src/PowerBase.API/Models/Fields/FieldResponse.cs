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
    public bool IsRequired { get; init; }
    public bool IsSystem { get; init; }
    public int DisplayOrder { get; init; }
    public DateTime CreatedOn { get; init; }
}
