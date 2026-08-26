using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pipelines.Commands.DeletePipeline;

public class DeletePipelineCommandHandler
{
    private readonly IPipelineRepository _pipelineRepo;
    private readonly IAuditRepository _auditRepo;

    public DeletePipelineCommandHandler(IPipelineRepository pipelineRepo, IAuditRepository auditRepo)
    {
        _pipelineRepo = pipelineRepo;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(DeletePipelineCommand command, CancellationToken ct = default)
    {
        var validator = new DeletePipelineCommandValidator();
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(
                validation.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        var pipeline = await _pipelineRepo.GetByPublicIdAsync(command.PublicId, ct);

        // Soft delete the pipeline metadata
        await _pipelineRepo.DeleteAsync(command.PublicId, ct);

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
            // No schedule to delete, safe to ignore
        }

        await _auditRepo.LogActivityAsync(
            AuditActions.Deleted,
            AuditEntityTypes.Pipeline,
            pipeline.PublicId.ToString(),
            $"Pipeline workflow deleted: {pipeline.Name}",
            appId: pipeline.AppId,
            ct: ct);
    }
}
