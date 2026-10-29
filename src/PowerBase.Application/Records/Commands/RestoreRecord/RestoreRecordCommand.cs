namespace PowerBase.Application.Records.Commands.RestoreRecord;

public record RestoreRecordCommand(Guid TablePublicId, Guid RecordPublicId);
