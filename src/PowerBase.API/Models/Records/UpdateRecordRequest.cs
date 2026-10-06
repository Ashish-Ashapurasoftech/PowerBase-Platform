using System.Text.Json;

namespace PowerBase.API.Models.Records;

public class UpdateRecordRequest
{
    public Dictionary<string, JsonElement> Fields { get; set; } = new();

    /// <summary>The report this write came from (the report grid's inline Grid Edit save) — when
    /// supplied, Form Rule enforcement narrows to that report's configured Grid Edit rules instead
    /// of every active rule on the table. Omitted by every other caller (the Add/Edit Record form,
    /// Quick Peek, child-record grids), which keeps today's full check unchanged. See
    /// UpdateRecordCommand.ReportId / FormRuleServerValidator.CollectViolationsAsync.</summary>
    public Guid? ReportId { get; set; }

    /// <summary>True only for the report grid's own inline Grid Edit save. The ONLY write that
    /// may set this — it's what allows ReportId (above) to narrow Form Rule enforcement, or skip
    /// it entirely when ReportId is absent. The Add/Edit Record form, Quick Peek, and child-record
    /// grids must never set this, and keep the full table-wide check unconditionally.</summary>
    public bool IsGridEditSave { get; set; }
}
