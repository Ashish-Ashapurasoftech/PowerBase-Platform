namespace PowerBase.Application.Fields.Commands.UpdateField;

public record UpdateFieldCommand(
    Guid TablePublicId,
    Guid FieldPublicId,
    string Name,
    string? Label,
    string? Description,
    bool IsRequired,
    string? DefaultValue,
    bool IsSearchable,
    bool IsSortable,
    bool IsFilterable,
    bool IsReportable);
