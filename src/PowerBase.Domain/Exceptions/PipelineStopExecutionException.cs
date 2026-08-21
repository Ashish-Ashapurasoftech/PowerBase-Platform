namespace PowerBase.Domain.Exceptions;

public class PipelineStopExecutionException : DomainException
{
    public PipelineStopExecutionException(string reason)
        : base("PIPELINE_STOPPED", $"Pipeline execution stopped: {reason}")
    {
    }
}
