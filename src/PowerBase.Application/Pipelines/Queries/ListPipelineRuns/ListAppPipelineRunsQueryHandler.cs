using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pipelines.Queries.ListPipelineRuns;

public class ListAppPipelineRunsQueryHandler
{
    private readonly IPipelineRepository _pipelineRepo;
    private readonly IAppRepository _appRepo;
    private readonly IUserRepository _userRepo;

    public ListAppPipelineRunsQueryHandler(IPipelineRepository pipelineRepo, IAppRepository appRepo, IUserRepository userRepo)
    {
        _pipelineRepo = pipelineRepo;
        _appRepo = appRepo;
        _userRepo = userRepo;
    }

    public async Task<AppPipelineRunsResult> HandleAsync(ListAppPipelineRunsQuery query, CancellationToken ct = default)
    {
        var appId = await _appRepo.GetIdByPublicIdAsync(query.AppPublicId, ct);
        if (appId <= 0)
            throw new NotFoundException("App", query.AppPublicId);

        long? pipelineId = null;
        if (query.PipelinePublicId is Guid pipelinePublicId)
        {
            var pipeline = await _pipelineRepo.GetByPublicIdAsync(pipelinePublicId, ct);
            if (pipeline == null || pipeline.AppId != appId)
                return new AppPipelineRunsResult(Array.Empty<AppPipelineRunDto>(), 0, query.Page < 1 ? 1 : query.Page, query.PageSize is < 1 or > 100 ? 10 : query.PageSize);
            pipelineId = pipeline.Id;
        }

        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize is < 1 or > 100 ? 10 : query.PageSize;

        // Dates arrive as local calendar days from the Activity filter; treat ToDate as inclusive
        // of the whole day so "today" actually captures runs up to midnight, not just 00:00:00.
        var toDate = query.ToDate?.Date.AddDays(1).AddTicks(-1);

        var totalCount = await _pipelineRepo.CountRunsByAppIdAsync(appId, pipelineId, query.FromDate, toDate, ct);
        var runs = await _pipelineRepo.GetRunsByAppIdAsync(appId, pipelineId, query.FromDate, toDate, page, pageSize, ct);

        var userIds = runs.Select(r => r.Run.TriggeredBy).Where(id => id != 0).Distinct().ToList();
        var userNames = new Dictionary<long, string>();
        if (userIds.Any())
        {
            var namesMap = await _userRepo.GetNamesByIdsAsync(userIds, ct);
            foreach (var kvp in namesMap)
            {
                userNames[kvp.Key] = kvp.Value;
            }
        }

        var items = runs.Select(r => new AppPipelineRunDto(
            r.Run.PublicId,
            r.PipelinePublicId,
            r.PipelineName,
            r.Run.Status,
            r.Run.TriggerType,
            r.Run.StartedOn,
            r.Run.CompletedOn,
            r.Run.TriggeredBy == 0 ? "System" : (userNames.TryGetValue(r.Run.TriggeredBy, out var name) ? name : $"User {r.Run.TriggeredBy}"),
            r.Run.ErrorMessage,
            r.Run.AttemptCount
        )).ToList();

        return new AppPipelineRunsResult(items, totalCount, page, pageSize);
    }
}
