namespace PowerBase.API.Models.Tables;

/// <summary>Slim shape for the tables list endpoint — full details (fields, key field, etc.)
/// are available via GET /tables/{publicId}.</summary>
public class TableListItemResponse
{
    public Guid PublicId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? SingularLabel { get; init; }
    public string? Icon { get; init; }
    public int RecordCount { get; init; }
    public int FieldCount { get; init; }
    public bool IsShowInBar { get; init; }
    public DateTime CreatedOn { get; init; }
}
