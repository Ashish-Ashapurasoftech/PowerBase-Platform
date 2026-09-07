namespace PowerBase.API.Models.Tables;

public record UpdateTableRequest(
    string Name,
    string? SingularLabel,
    string? PluralLabel,
    string? Description,
    string? Icon,
    long? DefaultRecordPickerField1Id = null,
    long? DefaultRecordPickerField2Id = null,
    long? DefaultRecordPickerField3Id = null,
    /// <summary>Null = leave unchanged; otherwise sets whether this table appears in the sidebar.</summary>
    bool? IsShowInBar = null,
    /// <summary>The table's Custom Data Rule formula, evaluated as a save-time gate on Add/Update
    /// (never Delete) — but only while <see cref="CustomDataRuleEnabled"/> is true. Rejected
    /// server-side if it fails to compile, but only when enabled — while off, it's stored as-is.</summary>
    string? CustomDataRule = null,
    /// <summary>The "Turn custom data rules on?" toggle.</summary>
    bool CustomDataRuleEnabled = false
);
