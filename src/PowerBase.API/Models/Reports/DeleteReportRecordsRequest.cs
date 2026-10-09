namespace PowerBase.API.Models.Reports;

public class DeleteReportRecordsRequest
{
    /// <summary>The same filter payload the grid sends to POST reports/{id}/run (page/sort/grouping are ignored).</summary>
    public RunReportRequest? Run { get; set; }

    /// <summary>False = preview (count only, nothing deleted). True = delete.</summary>
    public bool Confirm { get; set; }

    /// <summary>Required when Confirm is true — the matching count the user confirmed.</summary>
    public int? ExpectedCount { get; set; }
}

public class DeleteReportRecordsResponse
{
    public int MatchingCount { get; init; }
    public int MaxRecords { get; init; }
    public bool ExceedsLimit { get; init; }
    public bool Deleted { get; init; }
    public int DeletedCount { get; init; }
}
