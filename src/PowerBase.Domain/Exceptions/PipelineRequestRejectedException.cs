namespace PowerBase.Domain.Exceptions;

// A service rejected the request: terminal for retries, but recoverable by Handle Errors.
public sealed class PipelineRequestRejectedException : PipelineNonRetryableException
{
    public int StatusCode { get; }

    public PipelineRequestRejectedException(int statusCode)
        : base($"HTTP request failed with status {statusCode}.")
    {
        if (statusCode < 400 || statusCode >= 500 || statusCode == 429)
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        StatusCode = statusCode;
    }
}
