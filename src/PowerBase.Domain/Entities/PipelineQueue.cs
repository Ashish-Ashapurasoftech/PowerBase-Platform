using System;

namespace PowerBase.Domain.Entities;

public class PipelineQueue
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public Guid MessageId { get; set; }
    public long TenantId { get; set; }
    public Guid TenantPublicId { get; set; }
    public long PipelineId { get; set; }
    public Guid PipelinePublicId { get; set; }
    public string QueueSource { get; set; } = string.Empty; // Event, Manual, Schedule, Webhook
    public long? TriggerStepId { get; set; }
    public string? TriggerStepRefId { get; set; }
    public string? TriggerEvent { get; set; }
    public string TriggerPayloadJson { get; set; } = string.Empty;
    public byte[] PayloadHash { get; set; } = Array.Empty<byte>();
    public long? TriggeredBy { get; set; }
    public Guid? TriggerTablePublicId { get; set; }
    public Guid? CorrelationId { get; set; }
    public int Depth { get; set; } = 1;
    public string PipelineChain { get; set; } = "[]";
    public Guid? BatchId { get; set; }
    public string? VariablesJson { get; set; }
    public string PayloadVersion { get; set; } = "1.0";
    public DateTime EventTimestamp { get; set; }
    public string Status { get; set; } = "Pending"; // Pending, Processing, Succeeded, Skipped, Failed
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; } = 5;
    public DateTime? NextAttemptOn { get; set; }
    public string? LockedBy { get; set; }
    public DateTime? LockedUntil { get; set; }
    public Guid? ClaimToken { get; set; }
    public DateTime CreatedOn { get; set; }
    public DateTime? StartedOn { get; set; }
    public DateTime? CompletedOn { get; set; }
    public DateTime? FailedOn { get; set; }
    public DateTime? SkippedOn { get; set; }
    public DateTime LastModifiedOn { get; set; }
    public string? LastError { get; set; }
    public string? SkipReason { get; set; }
}
