namespace PowerBase.Application.Fields.Commands.BulkDeleteFields;

public record BulkDeleteFieldsCommand(Guid TablePublicId, IEnumerable<Guid> FieldPublicIds, bool Force = false);
