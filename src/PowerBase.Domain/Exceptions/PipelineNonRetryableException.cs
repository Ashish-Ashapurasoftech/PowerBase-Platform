namespace PowerBase.Domain.Exceptions;

public class PipelineNonRetryableException : DomainException
{
    public PipelineNonRetryableException(string message)
        : base("PIPELINE_NON_RETRYABLE_ERROR", message)
    {
    }
}
