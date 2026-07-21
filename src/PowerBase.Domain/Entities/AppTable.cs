namespace PowerBase.Domain.Entities;

public class AppTable
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long AppId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? SingularLabel { get; set; }
    public string? PluralLabel { get; set; }
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public string? PhysicalTableName { get; set; }
    public string DefaultReportSettings { get; set; } = "{}";
    public long? DisplayFieldId { get; set; }
    /// <summary>The field designated as this table's unique key for relationships. Null = Record ID# (default).
    /// Orthogonal to <see cref="DisplayFieldId"/> — the key drives Reference-column identity/joins, the display
    /// field drives label rendering only. Setting one never implicitly changes the other.</summary>
    public long? KeyFieldId { get; set; }
    public long? DefaultRecordPickerField1Id { get; set; }
    public long? DefaultRecordPickerField2Id { get; set; }
    public long? DefaultRecordPickerField3Id { get; set; }
    public int RecordCount { get; set; }
    public bool IsSystem { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedOn { get; set; }
    public long CreatedBy { get; set; }
    public DateTime? ModifiedOn { get; set; }
    public long? ModifiedBy { get; set; }
    public DateTime? DeletedOn { get; set; }
    public long? DeletedBy { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
    public List<AppField> Fields { get; set; } = new();
}
