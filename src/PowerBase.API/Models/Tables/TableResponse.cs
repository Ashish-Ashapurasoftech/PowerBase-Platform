using PowerBase.API.Models.Fields;

namespace PowerBase.API.Models.Tables;

public class TableResponse
{
    public Guid PublicId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? SingularLabel { get; init; }
    public string? PluralLabel { get; init; }
    public string? Description { get; init; }
    public string? Icon { get; init; }
    public string? PhysicalTableName { get; init; }
    public int RecordCount { get; init; }
    public int? FieldCount { get; init; }
    public DateTime CreatedOn { get; init; }
    public IReadOnlyList<FieldResponse> Fields { get; init; } = [];
}
