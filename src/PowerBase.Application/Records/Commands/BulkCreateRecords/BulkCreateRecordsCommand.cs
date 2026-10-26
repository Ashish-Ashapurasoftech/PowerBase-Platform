namespace PowerBase.Application.Records.Commands.BulkCreateRecords;

/// <summary>Adds several records to one table in a single all-or-nothing save — the new rows a user
/// added (or pasted) in a report's Grid Edit.</summary>
/// <param name="Rows">Each new row's submitted values by Fid, in the order the user entered them. A
/// field a row leaves out gets its default value, exactly as in a single-record create.</param>
/// <param name="ReportId">The report the rows were added from. Together with <paramref name="IsGridEditSave"/>
/// it narrows Form Rule checks to that report's Grid Edit rules, the same as a Grid Edit update.</param>
public record BulkCreateRecordsCommand(
    Guid TablePublicId,
    IReadOnlyList<IReadOnlyDictionary<long, object?>> Rows,
    Guid? ReportId = null,
    bool IsGridEditSave = false);
