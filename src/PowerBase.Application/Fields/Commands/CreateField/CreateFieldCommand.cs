namespace PowerBase.Application.Fields.Commands.CreateField;

public record CreateFieldCommand(
    Guid TablePublicId,
    string TypeCode,
    string Name,
    string? Label,
    string? Description,
    bool IsRequired,
    string? Settings = null);
