namespace PowerBase.Application.Fields.Commands.DeleteField;

public record DeleteFieldCommand(Guid TablePublicId, Guid FieldPublicId);
