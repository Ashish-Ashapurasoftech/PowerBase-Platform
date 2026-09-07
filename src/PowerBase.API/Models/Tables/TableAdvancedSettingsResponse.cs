namespace PowerBase.API.Models.Tables;

/// <summary>Slim shape for the table Advanced Settings page — only the table's own editable
/// properties plus the minimal per-field data needed to populate the "Identifying Records"
/// (default record picker) dropdowns. Full field config (Settings, DefaultValue, IsRequired,
/// IsSearchable, etc.) is not needed there — see FieldsController.List for the fields grid.</summary>
public class TableAdvancedSettingsResponse
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
    public long? DefaultRecordPickerField1Id { get; set; }
    public long? DefaultRecordPickerField2Id { get; set; }
    public long? DefaultRecordPickerField3Id { get; set; }
    /// <summary>Optional formula evaluated as a save-time gate on every Add/Update to this table
    /// — but only while <see cref="IsCustomDataRuleEnabled"/> is true.</summary>
    public string? CustomDataRule { get; init; }
    /// <summary>The "Turn custom data rules on?" toggle.</summary>
    public bool IsCustomDataRuleEnabled { get; init; }
    public IReadOnlyList<TableAdvancedSettingsFieldResponse> Fields { get; init; } = [];
}

/// <summary>Just enough per-field data to populate the default record picker dropdowns.</summary>
public class TableAdvancedSettingsFieldResponse
{
    public long Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool IsSystem { get; init; }
}
