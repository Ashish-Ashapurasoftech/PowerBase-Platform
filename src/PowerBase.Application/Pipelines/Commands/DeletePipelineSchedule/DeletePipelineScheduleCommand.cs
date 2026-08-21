using System;

namespace PowerBase.Application.Pipelines.Commands.DeletePipelineSchedule;

public class DeletePipelineScheduleCommand
{
    public Guid PipelinePublicId { get; }

    public DeletePipelineScheduleCommand(Guid pipelinePublicId)
    {
        PipelinePublicId = pipelinePublicId;
    }
}
