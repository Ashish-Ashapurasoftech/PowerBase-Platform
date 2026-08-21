using System;

namespace PowerBase.Domain.Entities;

public class PipelineOutboxItem
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long PipelineId { get; set; }
    public string TriggerEvent { get; set; } = string.Empty;
    public string TriggerPayloadJson { get; set; } = string.Empty;
    public long TriggeredBy { get; set; }
    public Guid TriggerTablePublicId { get; set; }
    public Guid CorrelationId { get; set; }
    public int Depth { get; set; }
    public string PipelineChain { get; set; } = "[]";
    public Guid MessageId { get; set; }
    public Guid BatchId { get; set; }
    public string PayloadVersion { get; set; } = "1.0";
    public DateTime CreatedOn { get; set; }
    public byte Published { get; set; }
    public DateTime? PublishedOn { get; set; }
    public DateTime? FailedOn { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? NextAttemptOn { get; set; }
    public string? LastError { get; set; }
    public string? LockedBy { get; set; }
    public DateTime? LockedUntil { get; set; }
}
