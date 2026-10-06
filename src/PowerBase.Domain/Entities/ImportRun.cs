namespace PowerBase.Domain.Entities;

/// <summary>One execution of an <see cref="ImportDefinition"/>. Append-only log row whose counters are
/// advanced chunk by chunk while the run is in progress.</summary>
public class ImportRun
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long ImportDefinitionId { get; set; }
    public string TriggeredBy { get; set; } = "manual";
    public long TriggeredByUserId { get; set; }
    /// <summary>SHA-256 (hex) of the client token a caller of the run API sent; a repeat of the same token returns this run.</summary>
    public string? IdempotencyKey { get; set; }
    public string Status { get; set; } = "queued";
    public byte Progress { get; set; }
    public long RowsRead { get; set; }
    public long Inserted { get; set; }
    public long Updated { get; set; }
    public long Skipped { get; set; }
    public long Errored { get; set; }
    public long LastCommittedSourceId { get; set; }
    public long? SourceMaxId { get; set; }
    /// <summary>A user asked for the running import to stop; the worker sees it when it records its next chunk.</summary>
    public bool CancelRequested { get; set; }
    public DateTime? StartedOn { get; set; }
    public DateTime? CompletedOn { get; set; }
    public string? ErrorDetail { get; set; }
    /// <summary>Storage path of the feedback file (every row not imported, with the reason). Internal: never sent to
    /// clients, who download it through an authorised endpoint.</summary>
    public string? FeedbackFileUrl { get; set; }
    public string DefinitionSnapshotJson { get; set; } = "{}";
    public DateTime CreatedOn { get; set; }
}

/// <summary>A source row that was not imported, with the reason. Capped per run in the database; the
/// full list is in the feedback file.</summary>
public class ImportRunIssue
{
    public long Id { get; set; }
    public long ImportRunId { get; set; }
    public long? SourceRowRef { get; set; }
    public int? ColumnFid { get; set; }
    public string Outcome { get; set; } = "errored";
    public string ReasonCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    /// <summary>For duplicates against the destination: the Record ID# of the existing record holding the value.</summary>
    public long? ExistingRecordRef { get; set; }
}
