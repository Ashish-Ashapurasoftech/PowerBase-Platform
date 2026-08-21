using System;

namespace PowerBase.Domain.Exceptions;

public class PipelineRunRunningException : Exception
{
    public PipelineRunRunningException(string message) : base(message)
    {
    }
}
