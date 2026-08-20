namespace PowerBase.API.Models.Tables;

/// <summary>Slim shape returned by Create/Update table — full details (fields, labels, key field, etc.)
/// are available via GET /tables/{publicId}; list details via GET /apps/{appId}/tables.</summary>
public class TableSummaryResponse
{
    public Guid PublicId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? SingularLabel { get; init; }
    public string? Icon { get; init; }
    public bool IsShowInBar { get; init; }
    public DateTime CreatedOn { get; init; }
}
