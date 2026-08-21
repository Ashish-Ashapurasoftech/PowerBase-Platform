using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IPipelineExecutionQueue
{
    void QueueTask(PipelineExecutionTask task);
}
