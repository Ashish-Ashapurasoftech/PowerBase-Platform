namespace PowerBase.Application.Records.Commands.UpdateRecord;

public record UpdateRecordCommand(
    Guid TablePublicId,
    Guid RecordPublicId,
    IReadOnlyDictionary<long, object?> FieldValues);
