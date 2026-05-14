namespace PowerBase.Application.Records.Commands.CreateRecord;

public record CreateRecordCommand(
    Guid TablePublicId,
    IReadOnlyDictionary<long, object?> FieldValues);
