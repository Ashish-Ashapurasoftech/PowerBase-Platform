using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pipelines.Commands.DeletePipelines;

public class DeletePipelinesCommandHandler
{
    private readonly IPipelineRepository _pipelineRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly IMainPipelineQueueRepository _queueRepo;
    private readonly IQueryContext _queryContext;
    private readonly IAppAccessService _appAccessService;

    public DeletePipelinesCommandHandler(
        IPipelineRepository pipelineRepo,
        IAuditRepository auditRepo,
        IMainPipelineQueueRepository queueRepo,
        IQueryContext queryContext,
        IAppAccessService appAccessService)
    {
        _pipelineRepo = pipelineRepo;
        _auditRepo = auditRepo;
        _queueRepo = queueRepo;
        _queryContext = queryContext;
        _appAccessService = appAccessService;
    }

    public async Task HandleAsync(DeletePipelinesCommand command, CancellationToken ct = default)
    {
        if (command.PipelinePublicIds == null || command.PipelinePublicIds.Count == 0)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["pipelinePublicIds"] = new[] { "At least one PowerFlow ID is required." }
            });
        }

        if (command.PipelinePublicIds.Any(id => id == Guid.Empty))
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["pipelinePublicIds"] = new[] { "Guid.Empty is not a valid PowerFlow ID." }
            });
        }

        var distinctIds = command.PipelinePublicIds.Distinct().ToList();

        var pipelines = new List<Pipeline>();
        foreach (var publicId in distinctIds)
        {
            var pipeline = await _pipelineRepo.GetByPublicIdAsync(publicId, ct);

            // A PowerFlow may be managed from an app other than the one that owns it, so
            // permission is checked against the pipeline's own app rather than the request's appId.
            await _appAccessService.RequirePermissionByPipelinePublicIdAsync(publicId, PermissionCodes.PowerFlowsDelete, ct);

            pipelines.Add(pipeline);
        }

        await _pipelineRepo.SoftDeleteManyAsync(distinctIds, ct);

        // Immediate best-effort queue terminalization (failure does not block response)
        try
        {
            var pipelineIds = pipelines.Select(p => p.Id).ToList();
            await _queueRepo.CancelPendingJobsForPipelinesAsync(_queryContext.TenantId, pipelineIds, "Pipeline deleted", ct);
        }
        catch (System.Exception ex)
        {
            // Suppress to guarantee Tenant DB delete API success
        }

        foreach (var pipeline in pipelines)
        {
            try
            {
                var schedule = await _pipelineRepo.GetScheduleByPipelineIdAsync(pipeline.Id, ct);
                if (schedule != null)
                {
                    await _pipelineRepo.DeleteScheduleAsync(schedule.PublicId, ct);
                }
            }
            catch (NotFoundException)
            {
                // Safe to ignore
            }
        }

        foreach (var pipeline in pipelines)
        {
            await _auditRepo.LogActivityAsync(
                AuditActions.Deleted,
                AuditEntityTypes.Pipeline,
                pipeline.PublicId.ToString(),
                $"Pipeline workflow deleted: {pipeline.Name}",
                appId: pipeline.AppId,
                ct: ct);
        }
    }
}
