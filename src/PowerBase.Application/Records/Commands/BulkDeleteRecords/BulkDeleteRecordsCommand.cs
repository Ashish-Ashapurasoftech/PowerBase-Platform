namespace PowerBase.Application.Records.Commands.BulkDeleteRecords;

/// <param name="MaxRecords">Per-call ceiling on RecordPublicIds. The public bulk-delete endpoint keeps the
/// default of 500; "Delete these records" on a report passes its own, higher ceiling.</param>
public record BulkDeleteRecordsCommand(Guid TablePublicId, IReadOnlyList<Guid> RecordPublicIds, int MaxRecords = 500);
