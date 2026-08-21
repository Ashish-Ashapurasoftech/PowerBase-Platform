namespace PowerBase.Domain.Exceptions;

public class PipelineRecursionException : DomainException
{
    public PipelineRecursionException(string message)
        : base("PIPELINE_RECURSION_LIMIT_EXCEEDED", message)
    {
    }
}
