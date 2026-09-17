using System;

namespace PowerBase.Domain.Exceptions;

public class PipelineWaitException : Exception
{
    public DateTime ResumeDate { get; }

    public PipelineWaitException(DateTime resumeDate) 
        : base($"Pipeline execution paused until {resumeDate:O}.")
    {
        ResumeDate = resumeDate;
    }
}
