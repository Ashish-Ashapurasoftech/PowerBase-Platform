namespace PowerBase.Application.Records.Commands.BulkDeleteRecords;

public record BulkDeleteRecordsCommand(Guid TablePublicId, IReadOnlyList<Guid> RecordPublicIds);
