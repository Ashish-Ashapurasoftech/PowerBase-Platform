using PowerBase.Application.Reports.Queries.RunReport;

namespace PowerBase.Application.Reports.Commands.DeleteReportRecords;

/// <param name="Query">The exact run request the grid used (saved filters, dynamic filters, quick search,
/// Advanced/column filters, ask-the-user answers). Page/PageSize/grouping are ignored.</param>
/// <param name="Confirm">False = preview only (count, nothing deleted). True = delete.</param>
/// <param name="ExpectedCount">Required when Confirm is true: the count the user was shown and agreed to.
/// If the live match count differs, nothing is deleted.</param>
public record DeleteReportRecordsCommand(RunReportQuery Query, bool Confirm, int? ExpectedCount);

public record DeleteReportRecordsResult(int MatchingCount, int MaxRecords, bool ExceedsLimit, bool Deleted, int DeletedCount);
