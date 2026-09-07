using PowerBase.API.Models.Fields;

namespace PowerBase.API.Models.Tables;

public class TableResponse
{
    public Guid PublicId { get; init; }
    public string Name { get; init; } = string.Empty;
    /// <summary>Stable formula reference for this table (<c>_DBID_{TABLE_NAME}</c>) — read-only,
    /// generated once at creation and never changed by a rename.</summary>
    public string Alias { get; init; } = string.Empty;
    public string? SingularLabel { get; init; }
    public string? PluralLabel { get; init; }
    public string? Description { get; init; }
    public string? Icon { get; init; }
    public string? PhysicalTableName { get; init; }
    public int RecordCount { get; set; }
    public int? FieldCount { get; set; }
    public bool IsShowInBar { get; set; }
    public long? DefaultRecordPickerField1Id { get; set; }
    public long? DefaultRecordPickerField2Id { get; set; }
    public long? DefaultRecordPickerField3Id { get; set; }
    /// <summary>The Fid of this table's key field (Set Key feature), or null when the key is the
    /// default Record ID#.</summary>
    public int? KeyFieldFid { get; set; }
    public DateTime CreatedOn { get; set; }
    public IReadOnlyList<FieldResponse> Fields { get; init; } = [];
}
