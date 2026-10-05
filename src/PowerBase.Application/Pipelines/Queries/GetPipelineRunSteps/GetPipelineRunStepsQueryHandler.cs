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
            throw new NotFoundException("PowerFlowRun", query.RunPublicId);

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
                sr.StepPublicIdSnapshot.HasValue && sr.StepPublicIdSnapshot.Value != Guid.Empty ? sr.StepPublicIdSnapshot.Value : stepExists ? step!.PublicId : Guid.Empty,
                !string.IsNullOrWhiteSpace(sr.StepRefIdSnapshot) ? sr.StepRefIdSnapshot : stepExists ? step!.RefId : string.Empty,
                !string.IsNullOrWhiteSpace(sr.StepLabelSnapshot) ? sr.StepLabelSnapshot : stepExists ? (!string.IsNullOrWhiteSpace(step!.Label) ? step!.Label : step!.RefId) : string.Empty,
                !string.IsNullOrWhiteSpace(sr.StepTypeSnapshot) ? sr.StepTypeSnapshot : stepExists ? step!.Type : string.Empty,
                !string.IsNullOrWhiteSpace(sr.StepSubtypeSnapshot) ? sr.StepSubtypeSnapshot : stepExists ? (step!.Subtype ?? string.Empty) : string.Empty,
                sr.Status,
                sr.StartedOn,
                sr.CompletedOn,
                sr.InputContext,
                sr.OutputContext,
                sr.LogMessage,
                sr.PipelineRunAttemptId,
                sr.ExecutionPath,
                sr.SequenceNumber,
                sr.TransactionOutcome,
                sr.ErrorType,
                sr.CompletedOn.HasValue ? Math.Max(0L, (long)(sr.CompletedOn.Value - sr.StartedOn).TotalMilliseconds) : null
            );
        }).ToList();

        return new PipelineStepRunsResultDto(items, totalCount, page, pageSize);
    }
}
