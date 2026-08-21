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
}
