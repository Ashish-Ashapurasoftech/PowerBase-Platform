namespace PowerBase.API.Models.Reports;

public class DeleteReportRecordsRequest
{
    /// <summary>The same filter payload the grid sends to POST reports/{id}/run (page/sort/grouping are ignored).</summary>
    public RunReportRequest? Run { get; set; }

    /// <summary>False = preview (count only, nothing deleted). True = queue the background delete job.</summary>
    public bool Confirm { get; set; }

    /// <summary>Required when Confirm is true — the matching count the user confirmed.</summary>
    public int? ExpectedCount { get; set; }
}

public class DeleteReportRecordsResponse
{
    public int MatchingCount { get; init; }
    /// <summary>Set once the delete job is queued; poll GET records/delete-jobs/{jobId}.</summary>
    public Guid? JobId { get; init; }
}

public class DeleteReportJobResponse
{
    public Guid JobId { get; init; }
    /// <summary>Pending | InProgress | Completed | Failed | Cancelled</summary>
    public string Status { get; init; } = string.Empty;
    public int Total { get; init; }
    public int Deleted { get; init; }
    public string? Error { get; init; }
    public DateTime CreatedOn { get; init; }
    public DateTime? StartedOn { get; init; }
    public DateTime? CompletedOn { get; init; }
}
