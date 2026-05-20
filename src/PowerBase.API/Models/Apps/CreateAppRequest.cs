namespace PowerBase.API.Models.Apps;

public class CreateAppRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public string? Color { get; set; }
    public string TableName { get; set; } = "Table 1";
    public string? TableSingularLabel { get; set; }
    public string? TablePluralLabel { get; set; }
    public string? TableIcon { get; set; }
    public string? TableDescription { get; set; }
}
