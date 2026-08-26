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
    private readonly IAppRepository _appRepo;
    private readonly IPipelineRepository _pipelineRepo;
    private readonly IAuditRepository _auditRepo;

    public DeletePipelinesCommandHandler(
        IAppRepository appRepo,
        IPipelineRepository pipelineRepo,
        IAuditRepository auditRepo)
    {
        _appRepo = appRepo;
        _pipelineRepo = pipelineRepo;
        _auditRepo = auditRepo;
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

        var appId = await _appRepo.GetIdByPublicIdAsync(command.AppPublicId, ct);

        var pipelines = new List<Pipeline>();
        foreach (var publicId in distinctIds)
        {
            var pipeline = await _pipelineRepo.GetByPublicIdAsync(publicId, ct);
            if (pipeline.AppId != appId)
            {
                throw new UnauthorizedActionException("One or more PowerFlows do not belong to this application.");
            }
            pipelines.Add(pipeline);
        }

        await _pipelineRepo.SoftDeleteManyAsync(distinctIds, ct);

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
                appId: appId,
                ct: ct);
        }
    }
}
