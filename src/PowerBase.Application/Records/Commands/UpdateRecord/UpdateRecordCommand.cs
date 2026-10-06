namespace PowerBase.Application.Records.Commands.UpdateRecord;

public record UpdateRecordCommand(
    Guid TablePublicId,
    Guid RecordPublicId,
    IReadOnlyDictionary<long, object?> FieldValues,
    /// <summary>The report this write came from (the report grid's inline Grid Edit save), if any
    /// — narrows FormRuleServerValidator's enforcement to that report's configured Grid Edit
    /// rules. Null for every other caller (the Add/Edit Record form, Quick Peek, child-record
    /// grids, or a direct API call with none supplied), which keeps checking every active rule on
    /// the table, unchanged. See FormRuleServerValidator.CollectViolationsAsync.</summary>
    Guid? ReportId = null,
    /// <summary>True only for the report grid's own inline Grid Edit save — the ONLY caller
    /// allowed to narrow (via ReportId) or fully skip (no ReportId) Form Rule enforcement. Every
    /// other write always gets the full table-wide check regardless of this flag or ReportId.
    /// See FormRuleServerValidator.CollectViolationsAsync.</summary>
    bool IsGridEditSave = false);
