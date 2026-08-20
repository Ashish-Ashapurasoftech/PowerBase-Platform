using System;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Pipelines.Commands.DeletePipelineSchedule;

public class DeletePipelineScheduleCommandHandler
{
    private readonly IPipelineRepository _pipelineRepo;

    public DeletePipelineScheduleCommandHandler(IPipelineRepository pipelineRepo)
    {
        _pipelineRepo = pipelineRepo;
    }

    public async Task HandleAsync(DeletePipelineScheduleCommand command, CancellationToken ct)
    {
        var pipelineId = await _pipelineRepo.GetIdByPublicIdAsync(command.PipelinePublicId, ct);
        var schedule = await _pipelineRepo.GetScheduleByPipelineIdAsync(pipelineId, ct);
        if (schedule != null)
        {
            await _pipelineRepo.DeleteScheduleAsync(schedule.PublicId, ct);
        }
    }
}
