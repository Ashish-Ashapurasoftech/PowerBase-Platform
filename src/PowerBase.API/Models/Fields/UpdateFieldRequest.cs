namespace PowerBase.API.Models.Fields;

public class UpdateFieldRequest
{
    public string? Label { get; set; }
    public string? Description { get; set; }
    public bool IsRequired { get; set; }
}
