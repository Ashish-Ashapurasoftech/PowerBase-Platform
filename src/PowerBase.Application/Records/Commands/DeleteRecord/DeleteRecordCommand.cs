namespace PowerBase.Application.Records.Commands.DeleteRecord;

public record DeleteRecordCommand(Guid TablePublicId, Guid RecordPublicId);
