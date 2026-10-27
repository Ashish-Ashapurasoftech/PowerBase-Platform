namespace PowerBase.API.Models.Fields;

/// <summary>Progress of a Snapshot field's "Reinitialize" background job.</summary>
public class FieldSnapshotJobResponse
{
    public Guid JobId { get; init; }
    /// <summary>Pending | InProgress | Completed | Failed | Cancelled.</summary>
    public string Status { get; init; } = string.Empty;
    public int TotalCount { get; init; }
    public int ProcessedCount { get; init; }
    /// <summary>Records whose value actually changed.</summary>
    public int ChangedCount { get; init; }
    public string? LastError { get; init; }
    public DateTime CreatedOn { get; init; }
    public DateTime? StartedOn { get; init; }
    public DateTime? CompletedOn { get; init; }
}
