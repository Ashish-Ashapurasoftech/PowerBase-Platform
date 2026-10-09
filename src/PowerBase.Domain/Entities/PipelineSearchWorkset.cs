using System;

namespace PowerBase.Domain.Entities;

public class PipelineSearchWorkset
{
    public Guid WorksetId { get; set; }
    public Guid RunMessageId { get; set; }
    public string StepRefId { get; set; } = string.Empty;
    public string Status { get; set; } = "Discovering";
    public long LastRecordId { get; set; }
    /// <summary>Where the matches are being read from: "Sql" (keyset on LastRecordId) or "Ai" (Azure AI Search, resumed from LastSearchCursor).</summary>
    public string DiscoverySource { get; set; } = "Sql";
    /// <summary>The AI id-range cursor after the last page staged; null before the first.</summary>
    public string? LastSearchCursor { get; set; }
    public long SnapshotMaxRecordId { get; set; }
    public int DiscoveredCount { get; set; }
    public DateTime CreatedOn { get; set; }
    public DateTime? DiscoveryCompletedOn { get; set; }
}
