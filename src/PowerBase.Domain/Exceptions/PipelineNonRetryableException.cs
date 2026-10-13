namespace PowerBase.Domain.Exceptions;

public class PipelineNonRetryableException : DomainException
{
    /// <summary>Structured details of the failure (beyond the message) for the run history, e.g. which records Copy Records rejected.
    /// Null for an ordinary failure.</summary>
    public IReadOnlyDictionary<string, object?>? FailureDetails { get; }

    public PipelineNonRetryableException(string message, IReadOnlyDictionary<string, object?>? failureDetails = null)
        : base("PIPELINE_NON_RETRYABLE_ERROR", message)
    {
        FailureDetails = failureDetails;
    }
}
