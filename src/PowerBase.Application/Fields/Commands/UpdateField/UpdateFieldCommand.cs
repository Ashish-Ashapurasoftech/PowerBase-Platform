namespace PowerBase.Application.Fields.Commands.UpdateField;

public record UpdateFieldCommand(
    Guid TablePublicId,
    int FieldFid,
    string Name,
    string? Label,
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
    string? Settings);
