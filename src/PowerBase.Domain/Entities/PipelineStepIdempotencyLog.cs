namespace PowerBase.Domain.Entities;

public class PipelineStepIdempotencyLog
{
    public Guid MessageId { get; set; }
    public Guid StepPublicId { get; set; }
    public byte[] ExecutionPathHash { get; set; } = Array.Empty<byte>();
    public string ExecutionPath { get; set; } = string.Empty;
    public string OutputJson { get; set; } = string.Empty;
    public DateTime CreatedOn { get; set; }
}
