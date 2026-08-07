namespace PowerBase.API.Models.Fields;

public class CreateFieldRequest
{
    public string TypeCode { get; set; } = string.Empty;
    /// <summary>User-facing display value. Name (the stable third-party identifier) is auto-generated from this.</summary>
    public string Label { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsRequired { get; set; }
    public string? DefaultValue { get; set; }
    public bool IsAuditable { get; set; } = false;
    public string? Settings { get; set; }
}
