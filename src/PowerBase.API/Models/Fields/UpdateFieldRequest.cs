namespace PowerBase.API.Models.Fields;

public record UpdateFieldRequest(
    string Label,
    string? Description,
    bool IsRequired,
    string? DefaultValue,
    bool IsSearchable,
    bool IsSortable,
    bool IsFilterable,
    bool IsReportable,
    bool IsAuditable,
    bool IsUnique,
    bool IsEncrypted,
    bool IsAutoFill,
    string? Settings,
    /// <summary>Required reason for this change — every field-settings update creates a new,
    /// immutable version (see meta.AppFieldVersion), and the commit message is the human-readable
    /// "why" attached to that version.</summary>
    string CommitMessage,
    /// <summary>Snapshot: Fid of the field whose value is captured once at record creation; null turns Snapshot off.</summary>
    int? SnapshotSourceFid = null,
    /// <summary>Snapshot: allow the captured value to be edited afterwards.</summary>
    bool SnapshotEditable = false,
    /// <summary>Snapshot: also capture the value for every existing record (queues the background Reinitialize job).</summary>
    bool InitializeSnapshotForExisting = false);
