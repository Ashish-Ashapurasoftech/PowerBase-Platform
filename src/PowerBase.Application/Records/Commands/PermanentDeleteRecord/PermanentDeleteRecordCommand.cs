namespace PowerBase.Application.Records.Commands.PermanentDeleteRecord;

public record PermanentDeleteRecordCommand(Guid TablePublicId, Guid RecordPublicId);
