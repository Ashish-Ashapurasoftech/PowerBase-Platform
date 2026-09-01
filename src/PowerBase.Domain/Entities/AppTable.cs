namespace PowerBase.Domain.Entities;

public class AppTable
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long AppId { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>Stable table reference for formulas — <c>_DBID_{TABLE_NAME}</c>, generated once at
    /// creation time (see <see cref="Constants.TableAliasNaming"/>) and never changed by a rename.
    /// Unique within the app. Used as a first-class <c>[_DBID_*]</c> token by the formula engine's
    /// cross-table functions (GetRecords, …) — see <c>AppTableAliasSchema</c>.</summary>
    public string Alias { get; set; } = string.Empty;
    public string? SingularLabel { get; set; }
    public string? PluralLabel { get; set; }
    public string? Description { get; set; }
    public string? Icon { get; set; }
    /// <summary>Optional formula evaluated as a save-time gate on every Add/Update to this table
    /// (never on Delete or other tables' writes) — but only while <see cref="IsCustomDataRuleEnabled"/>
    /// is true. A non-blank Text result blocks the save and is shown to the user as the violation
    /// message. See <c>CustomDataRuleValidator</c>.</summary>
    public string? CustomDataRule { get; set; }
    /// <summary>The "Turn custom data rules on?" toggle. While false, <see cref="CustomDataRule"/>
    /// is stored as-is (not even syntax-validated) but never evaluated on record writes — lets an
    /// admin draft/save an incomplete formula before switching enforcement on.</summary>
    public bool IsCustomDataRuleEnabled { get; set; }
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
    /// <summary>Whether this table appears in the sidebar/navigation bar. Defaults to true.</summary>
    public bool IsShowInBar { get; set; } = true;
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
