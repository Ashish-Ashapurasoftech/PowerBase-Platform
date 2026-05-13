using PowerBase.Domain.Enums;

namespace PowerBase.Domain.Entities;

public class FieldType
{
    public long Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string SqlType { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}
