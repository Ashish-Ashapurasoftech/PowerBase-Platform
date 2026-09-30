using System;

namespace PowerBase.Domain.Entities;

public class PipelineSearchWorkset
{
    public Guid WorksetId { get; set; }
    public Guid RunMessageId { get; set; }
    public string StepRefId { get; set; } = string.Empty;
    public string Status { get; set; } = "Discovering";
    public long LastRecordId { get; set; }
    public long SnapshotMaxRecordId { get; set; }
    public int DiscoveredCount { get; set; }
    public DateTime CreatedOn { get; set; }
    public DateTime? DiscoveryCompletedOn { get; set; }
}
