using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pipelines.Commands.CreatePipeline;

public class CreatePipelineCommandHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IPipelineRepository _pipelineRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly IQueryContext _queryContext;

    public CreatePipelineCommandHandler(
        IAppRepository appRepo,
        IPipelineRepository pipelineRepo,
        IAuditRepository auditRepo,
        IQueryContext queryContext)
    {
        _appRepo = appRepo;
        _pipelineRepo = pipelineRepo;
        _auditRepo = auditRepo;
        _queryContext = queryContext;
    }

    public async Task<CreatePipelineResult> HandleAsync(CreatePipelineCommand command, CancellationToken ct = default)
    {
        var validator = new CreatePipelineCommandValidator();
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(
                validation.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        var appId = await _appRepo.GetIdByPublicIdAsync(command.AppPublicId, ct);

        if (await _pipelineRepo.NameExistsForUserAsync(_queryContext.UserId, command.Name, ct))
            throw new DuplicateException("PowerFlow", "name", command.Name);

        var pipeline = new Pipeline
        {
            AppId = appId,
            Name = command.Name,
            Description = command.Description,
            IsActive = false,
            CreatedOn = DateTime.UtcNow,
            CreatedBy = _queryContext.UserId
        };

        var (publicId, id) = await _pipelineRepo.CreateAsync(pipeline, null, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.Created,
            AuditEntityTypes.Pipeline,
            publicId.ToString(),
            $"Pipeline workflow added: {command.Name}",
            appId: appId,
            ct: ct);

        return new CreatePipelineResult(publicId, pipeline.Name, pipeline.Description, pipeline.IsActive, pipeline.CreatedOn);
    }
}
