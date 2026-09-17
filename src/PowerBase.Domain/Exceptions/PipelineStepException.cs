namespace PowerBase.Domain.Exceptions;

public class PipelineStepException : DomainException
{
    public PipelineStepException(string message)
        : base("PIPELINE_STEP_ERROR", message)
    {
    }
}
