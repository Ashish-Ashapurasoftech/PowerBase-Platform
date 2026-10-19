namespace PowerBase.Application.Records.Commands.BulkDeleteRecords;

/// <param name="MaxRecords">Per-call ceiling on RecordPublicIds. The public bulk-delete endpoint keeps the
/// default of 500; the "Delete these records" background job deletes in chunks of at most 200.</param>
/// <param name="SkipAudit">True when the caller writes one summary audit entry for many chunks itself.</param>
/// <param name="SkipRecordsNotOwned">For a user limited to deleting their own records: true quietly leaves the records
/// they did not create alone and deletes the rest (the background job); false (default) rejects the whole call.</param>
public record BulkDeleteRecordsCommand(Guid TablePublicId, IReadOnlyList<Guid> RecordPublicIds, int MaxRecords = 500, bool SkipAudit = false, bool SkipRecordsNotOwned = false);
