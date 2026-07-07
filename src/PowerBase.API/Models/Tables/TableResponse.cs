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
    public int RecordCount { get; set; }
    public int? FieldCount { get; set; }
    public long? DefaultRecordPickerField1Id { get; set; }
    public long? DefaultRecordPickerField2Id { get; set; }
    public long? DefaultRecordPickerField3Id { get; set; }
    public DateTime CreatedOn { get; set; }
    public IReadOnlyList<FieldResponse> Fields { get; init; } = [];
}
