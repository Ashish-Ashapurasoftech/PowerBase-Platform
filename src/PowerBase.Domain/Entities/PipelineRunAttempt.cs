using System;

namespace PowerBase.Domain.Entities;

public class PipelineRunAttempt
{
    public long Id { get; set; }
    public long PipelineRunId { get; set; }
    public int AttemptNumber { get; set; }
    public string Status { get; set; } = string.Empty; // 'Running', 'Success', 'Failed'
    public DateTime StartedOn { get; set; }
    public DateTime? CompletedOn { get; set; }
    public string? LastError { get; set; }
}
