namespace PowerBase.API.Models.Apps;

public class CreateAppVariableRequest
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string? Description { get; set; }
}
