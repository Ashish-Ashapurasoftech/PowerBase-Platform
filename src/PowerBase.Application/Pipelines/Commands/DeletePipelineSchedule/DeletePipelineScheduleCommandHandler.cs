using System;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Pipelines.Commands.DeletePipelineSchedule;

public class DeletePipelineScheduleCommandHandler
{
    private readonly IPipelineRepository _pipelineRepo;
    private readonly IQueryContext _queryContext;

    public DeletePipelineScheduleCommandHandler(IPipelineRepository pipelineRepo, IQueryContext queryContext)
    {
        _pipelineRepo = pipelineRepo;
        _queryContext = queryContext;
    }

    public async Task HandleAsync(DeletePipelineScheduleCommand command, CancellationToken ct)
    {
        var pipelineId = await _pipelineRepo.GetIdByPublicIdAsync(command.PipelinePublicId, ct);
        var pipeline = await _pipelineRepo.GetByPublicIdAsync(command.PipelinePublicId, ct);
        var schedule = await _pipelineRepo.GetScheduleByPipelineIdAsync(pipelineId, ct);
        if (schedule != null)
        {
            await _pipelineRepo.DeleteScheduleAsync(schedule.PublicId, ct);
            if (pipeline.IsActive)
            {
                pipeline.IsActive = false;
                pipeline.ModifiedOn = DateTime.UtcNow;
                pipeline.ModifiedBy = _queryContext.UserId;
                await _pipelineRepo.UpdateAsync(pipeline, null, ct);
            }
        }
    }
}
