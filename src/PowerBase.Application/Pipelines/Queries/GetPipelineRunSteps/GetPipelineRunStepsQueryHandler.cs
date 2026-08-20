using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pipelines.Queries.GetPipelineRunSteps;

public class GetPipelineRunStepsQueryHandler
{
    private readonly IPipelineRepository _pipelineRepo;

    public GetPipelineRunStepsQueryHandler(IPipelineRepository pipelineRepo)
    {
        _pipelineRepo = pipelineRepo;
    }

    public async Task<IReadOnlyList<PipelineStepRunDto>> HandleAsync(GetPipelineRunStepsQuery query, CancellationToken ct = default)
    {
        var run = await _pipelineRepo.GetRunByPublicIdAsync(query.RunPublicId, ct);
        if (run == null)
            throw new NotFoundException("PipelineRun", query.RunPublicId);

        var steps = await _pipelineRepo.GetStepsByPipelineIdAsync(run.PipelineId, ct);
        var stepsMap = steps.ToDictionary(s => s.Id);

        var stepRuns = await _pipelineRepo.GetStepRunsByRunIdAsync(run.Id, ct);

        var items = stepRuns.Select(sr => {
            var stepExists = stepsMap.TryGetValue(sr.StepId, out var step);
            return new PipelineStepRunDto(
                sr.Id,
                stepExists ? step!.PublicId : Guid.Empty,
                stepExists ? step!.RefId : string.Empty,
                stepExists ? (!string.IsNullOrWhiteSpace(step!.Label) ? step!.Label : step!.RefId) : string.Empty,
                stepExists ? step!.Type : string.Empty,
                stepExists ? (step!.Subtype ?? string.Empty) : string.Empty,
                sr.Status,
                sr.StartedOn,
                sr.CompletedOn,
                sr.InputContext,
                sr.OutputContext,
                sr.LogMessage
            );
        }).ToList();

        return items;
    }
}
