using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pipelines.Queries.ListPipelineRuns;

public class ListPipelineRunsQueryHandler
{
    private readonly IPipelineRepository _pipelineRepo;
    private readonly IUserRepository _userRepo;

    public ListPipelineRunsQueryHandler(IPipelineRepository pipelineRepo, IUserRepository userRepo)
    {
        _pipelineRepo = pipelineRepo;
        _userRepo = userRepo;
    }

    public async Task<PipelineRunsResult> HandleAsync(ListPipelineRunsQuery query, CancellationToken ct = default)
    {
        var pipeline = await _pipelineRepo.GetByPublicIdAsync(query.PipelinePublicId, ct);
        if (pipeline == null)
            throw new NotFoundException("PowerFlow", query.PipelinePublicId);

        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize is < 1 or > 100 ? 10 : query.PageSize;

        var totalCount = await _pipelineRepo.CountRunsByPipelineIdAsync(pipeline.Id, ct);
        var runs = await _pipelineRepo.GetRunsByPipelineIdAsync(pipeline.Id, page, pageSize, ct);

        // Fetch display names of all unique users who triggered the runs to avoid N+1 query issue
        var userIds = runs.Select(r => r.TriggeredBy).Where(id => id != 0).Distinct().ToList();
        var userNames = new Dictionary<long, string>();
        if (userIds.Any())
        {
            var namesMap = await _userRepo.GetNamesByIdsAsync(userIds, ct);
            foreach (var kvp in namesMap)
            {
                userNames[kvp.Key] = kvp.Value;
            }
        }

        var items = runs.Select(r => new PipelineRunDto(
            r.PublicId,
            r.Status,
            r.TriggerType,
            r.StartedOn,
            r.CompletedOn,
            r.TriggeredBy == 0 ? "System" : (userNames.TryGetValue(r.TriggeredBy, out var name) ? name : $"User {r.TriggeredBy}"),
            r.ErrorMessage,
            r.AttemptCount
        )).ToList();

        return new PipelineRunsResult(items, totalCount, page, pageSize);
    }
}
