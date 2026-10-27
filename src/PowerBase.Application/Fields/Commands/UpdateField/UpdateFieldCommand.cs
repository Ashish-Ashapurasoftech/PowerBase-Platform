namespace PowerBase.Application.Fields.Commands.UpdateField;

public record UpdateFieldCommand(
    Guid TablePublicId,
    Guid FieldPublicId,
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
    string CommitMessage,
    int? SnapshotSourceFid = null,
    bool SnapshotEditable = false,
    /// <summary>Shared values; null leaves the stored flag unchanged (callers that predate the setting, e.g. imports).</summary>
    bool? IsSharedValues = null,
    bool InitializeSnapshotForExisting = false);
