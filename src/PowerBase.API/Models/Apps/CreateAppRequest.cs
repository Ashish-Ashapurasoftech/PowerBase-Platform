namespace PowerBase.API.Models.Apps;

public class CreateTableDto
{
    public string Name { get; set; } = "Table 1";
    public string? SingularLabel { get; set; }
    public string? PluralLabel { get; set; }
    public string? Icon { get; set; }
    public string? Description { get; set; }
    public string? Config { get; set; }
    public List<CreateAppFieldDto>? Fields { get; set; }
}

public class CreateAppFieldDto
{
    public string Name { get; set; } = string.Empty;
    public string TypeCode { get; set; } = "Text";
    public string? Settings { get; set; }
    public bool IsEncrypted { get; set; } = false;
}

public class CreateAppRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public string? Color { get; set; }
    public bool IsEncrypted { get; set; } = false;
    public List<CreateTableDto> Tables { get; set; } = new();
}
