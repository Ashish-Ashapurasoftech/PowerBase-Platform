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

    public async Task<PipelineStepRunsResultDto> HandleAsync(GetPipelineRunStepsQuery query, CancellationToken ct = default)
    {
        var run = await _pipelineRepo.GetRunByPublicIdAsync(query.RunPublicId, ct);
        if (run == null)
            throw new NotFoundException("PipelineRun", query.RunPublicId);

        var steps = await _pipelineRepo.GetStepsByPipelineIdAsync(run.PipelineId, ct);
        var stepsMap = steps.ToDictionary(s => s.Id);

        var page = query.Page > 0 ? query.Page : 1;
        var pageSize = query.PageSize > 0 ? query.PageSize : 50;
        if (pageSize > 200) pageSize = 200; // Enforce MaxPageSize limit

        var totalCount = await _pipelineRepo.CountStepRunsByRunIdAsync(run.Id, ct);
        var stepRuns = await _pipelineRepo.GetStepRunsByRunIdAsync(run.Id, page, pageSize, ct);

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

        return new PipelineStepRunsResultDto(items, totalCount, page, pageSize);
    }
}
