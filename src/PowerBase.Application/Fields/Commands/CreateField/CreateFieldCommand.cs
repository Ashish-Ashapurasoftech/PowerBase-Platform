namespace PowerBase.Application.Fields.Commands.CreateField;

public record CreateFieldCommand(
    Guid TablePublicId,
    string TypeCode,
    string Label,
    string? Description,
    bool IsRequired,
    bool IsAuditable = false,
    string? Settings = null,
    string? DefaultValue = null);
