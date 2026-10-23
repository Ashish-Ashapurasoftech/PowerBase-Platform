namespace PowerBase.API.Models.Tables;

public class CreateTableRequest
{
    public string Name { get; set; } = string.Empty;
    public string? SingularLabel { get; set; }
    public string? PluralLabel { get; set; }
    public string? Description { get; set; }
    public string? Icon { get; set; }
    /// <summary>Optional custom fields to create together with the table.</summary>
    public List<CreateTableFieldRequest>? Fields { get; set; }
}

public class CreateTableFieldRequest
{
    public string Label { get; set; } = string.Empty;
    public string TypeCode { get; set; } = string.Empty;
    public string? Settings { get; set; }
    public bool IsRequired { get; set; }
    public bool IsEncrypted { get; set; }
}
