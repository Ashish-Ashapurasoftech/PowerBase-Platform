using System;

namespace PowerBase.Domain.Entities;

public class PipelineRun
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long PipelineId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string TriggerType { get; set; } = string.Empty;
    public DateTime StartedOn { get; set; }
    public DateTime? CompletedOn { get; set; }
    public long TriggeredBy { get; set; }
    public string? ErrorMessage { get; set; }
    public Guid? MessageId { get; set; }
    public int AttemptCount { get; set; } = 1;
    public DateTime? HeartbeatOn { get; set; }
    public string? LockedBy { get; set; }
    public DateTime? LockedUntil { get; set; }
    public string? LastError { get; set; }
}
