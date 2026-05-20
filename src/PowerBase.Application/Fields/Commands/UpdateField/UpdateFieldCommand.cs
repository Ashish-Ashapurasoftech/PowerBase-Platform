namespace PowerBase.Application.Fields.Commands.UpdateField;

public record UpdateFieldCommand(Guid TablePublicId, Guid FieldPublicId, string Name, string? Label, string? Description);
