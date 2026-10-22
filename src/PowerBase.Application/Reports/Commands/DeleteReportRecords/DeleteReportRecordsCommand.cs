using PowerBase.Application.Reports.Queries.RunReport;

namespace PowerBase.Application.Reports.Commands.DeleteReportRecords;

/// <param name="Query">The exact run request the grid used (saved filters, dynamic filters, quick search,
/// Advanced/column filters, ask-the-user answers). Page/PageSize/grouping are ignored.</param>
/// <param name="Confirm">False = preview only (count, nothing deleted). True = queue the delete job.</param>
/// <param name="ExpectedCount">Required when Confirm is true: the count the user was shown and agreed to.
/// If the live match count differs, no job is created.</param>
public record DeleteReportRecordsCommand(RunReportQuery Query, bool Confirm, int? ExpectedCount);

/// <param name="MatchingCount">Live number of records the report matches right now.</param>
/// <param name="JobId">Set when a delete job was queued (Confirm = true). Poll it for progress.</param>
public record DeleteReportRecordsResult(int MatchingCount, Guid? JobId);

public record DeleteJobStatusResult(Guid JobId, string Status, int Total, int Deleted, string? Error, DateTime CreatedOn, DateTime? StartedOn, DateTime? CompletedOn);
