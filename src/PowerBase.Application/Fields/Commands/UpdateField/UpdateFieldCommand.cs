namespace PowerBase.Application.Fields.Commands.UpdateField;

public record UpdateFieldCommand(
    Guid TablePublicId,
    long FieldId,
    string? Label,
    string? Description,
    bool IsRequired);
