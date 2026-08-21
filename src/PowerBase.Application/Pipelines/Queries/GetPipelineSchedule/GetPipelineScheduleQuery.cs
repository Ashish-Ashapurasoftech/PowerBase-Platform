using System;

namespace PowerBase.Application.Pipelines.Queries.GetPipelineSchedule;

public class GetPipelineScheduleQuery
{
    public Guid PipelinePublicId { get; }

    public GetPipelineScheduleQuery(Guid pipelinePublicId)
    {
        PipelinePublicId = pipelinePublicId;
    }
}
