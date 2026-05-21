namespace PowerBase.API.Models.Apps;

public class UpdateAppVariableRequest
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string? Description { get; set; }
}
