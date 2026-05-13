namespace PowerBase.Domain.Entities;

public class FieldType
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string SqlDataType { get; set; } = string.Empty;
    public bool SupportsDefault { get; set; }
    public bool SupportsRequired { get; set; }
    public bool SupportsUnique { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; }
}
