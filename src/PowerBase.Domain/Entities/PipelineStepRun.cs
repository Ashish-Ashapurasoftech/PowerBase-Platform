namespace PowerBase.Domain.Entities;

public class PipelineStepRun
{
    public long Id { get; set; }
    public long PipelineRunId { get; set; }
    public long StepId { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime StartedOn { get; set; }
    public DateTime? CompletedOn { get; set; }
    public string? InputContext { get; set; }
    public string? OutputContext { get; set; }
    public string? LogMessage { get; set; }
    public long? PipelineRunAttemptId { get; set; }
    public string? ExecutionPath { get; set; }
    public int SequenceNumber { get; set; }
    public string TransactionOutcome { get; set; } = "NotApplicable";
    public string? ErrorType { get; set; }
    public Guid? StepPublicIdSnapshot { get; set; }
    public string StepRefIdSnapshot { get; set; } = string.Empty;
    public string StepLabelSnapshot { get; set; } = string.Empty;
    public string StepTypeSnapshot { get; set; } = string.Empty;
    public string StepSubtypeSnapshot { get; set; } = string.Empty;
}
