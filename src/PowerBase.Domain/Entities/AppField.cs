namespace PowerBase.Domain.Entities;

public class AppField
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public long AppTableId { get; set; }
    public long FieldTypeId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsRequired { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
