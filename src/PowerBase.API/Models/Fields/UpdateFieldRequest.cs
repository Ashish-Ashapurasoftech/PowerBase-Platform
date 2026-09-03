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
    string? Settings,
    /// <summary>Required reason for this change — every field-settings update creates a new,
    /// immutable version (see meta.AppFieldVersion), and the commit message is the human-readable
    /// "why" attached to that version.</summary>
    string CommitMessage);
