namespace PowerBase.Application.Fields.Commands.DeleteField;

public record DeleteFieldCommand(Guid TablePublicId, int FieldFid, bool Force = false);
