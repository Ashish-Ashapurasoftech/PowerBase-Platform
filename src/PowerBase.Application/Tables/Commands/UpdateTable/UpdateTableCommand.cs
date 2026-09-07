namespace PowerBase.Application.Tables.Commands.UpdateTable;

public record UpdateTableCommand(
    Guid TablePublicId,
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
    /// <summary>The table's Custom Data Rule formula — like Name/Description, always set to
    /// whatever is passed (null/blank both mean "no rule"), not the "leave unchanged unless
    /// supplied" convention <see cref="IsShowInBar"/> uses. Only syntax-validated server-side (and
    /// rejected if invalid) when <see cref="CustomDataRuleEnabled"/> is true — see
    /// <see cref="UpdateTableCommandHandler"/>.</summary>
    string? CustomDataRule = null,
    /// <summary>The "Turn custom data rules on?" toggle. While false, CustomDataRule is stored
    /// as-is — not even syntax-validated — and never evaluated on record writes (see
    /// <c>PowerBase.Application.Records.CustomDataRuleValidator</c>).</summary>
    bool CustomDataRuleEnabled = false
);
