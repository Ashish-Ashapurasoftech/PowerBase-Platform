namespace PowerBase.Application.Reports.Queries.ListReports;

/// <param name="IncludeHidden">Also list Hidden reports. Off by default: they are unlisted everywhere except where a report is being picked
/// (the dashboard designer).</param>
public record ListReportsQuery(Guid AppPublicId, bool IncludeHidden = false);
