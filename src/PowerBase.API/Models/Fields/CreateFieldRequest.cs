namespace PowerBase.API.Models.Fields;

public class CreateFieldRequest
{
    public string TypeCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Label { get; set; }
    public string? Description { get; set; }
    public bool IsRequired { get; set; }
    public string? DefaultValue { get; set; }
    public bool IsAuditable { get; set; } = true;
    public bool IsEncrypted { get; set; } = false;
    public string? Settings { get; set; }
}
