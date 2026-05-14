namespace PowerBase.API.Models.Tables;

public class CreateTableRequest
{
    public string Name { get; set; } = string.Empty;
    public string? SingularLabel { get; set; }
    public string? PluralLabel { get; set; }
    public string? Description { get; set; }
    public string? Icon { get; set; }
}
