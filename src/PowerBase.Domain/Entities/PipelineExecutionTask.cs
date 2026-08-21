namespace PowerBase.Domain.Entities;

public class PipelineExecutionTask
{
    public long TenantId { get; set; }
    public long PipelineId { get; set; }
    public string TriggerEvent { get; set; } = string.Empty;
    public string TriggerPayloadJson { get; set; } = string.Empty;
    public long TriggeredBy { get; set; }
    public Guid? TriggerTablePublicId { get; set; }
    public string? VariablesJson { get; set; }
    public string? CorrelationId { get; set; }
    public int Depth { get; set; } = 1;
    public string? MessageId { get; set; }
    public string? WorkerId { get; set; }
}

