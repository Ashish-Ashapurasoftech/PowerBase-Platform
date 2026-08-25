using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pipelines.Commands.UpdatePipeline;

public class UpdatePipelineCommandHandler
{
    private readonly IPipelineRepository _pipelineRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly IQueryContext _queryContext;
    private readonly IAppRepository _appRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IAppAccessService _appAccessService;
    private readonly ITenantRepository _tenantRepo;
    private readonly IServiceProvider _serviceProvider;

    public UpdatePipelineCommandHandler(
        IPipelineRepository pipelineRepo,
        IAuditRepository auditRepo,
        IQueryContext queryContext,
        IAppRepository appRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IAppAccessService appAccessService,
        ITenantRepository tenantRepo,
        IServiceProvider serviceProvider)
    {
        _pipelineRepo = pipelineRepo;
        _auditRepo = auditRepo;
        _queryContext = queryContext;
        _appRepo = appRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _appAccessService = appAccessService;
        _tenantRepo = tenantRepo;
        _serviceProvider = serviceProvider;
    }

    public async Task HandleAsync(UpdatePipelineCommand command, CancellationToken ct = default)
    {
        var validator = new UpdatePipelineCommandValidator();
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(
                validation.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        var pipeline = await _pipelineRepo.GetByPublicIdAsync(command.PublicId, ct);
        bool isTransitioningToActive = !pipeline.IsActive && command.IsActive;

        if (command.IsActive)
        {
            var steps = await _pipelineRepo.GetStepsByPipelineIdAsync(pipeline.Id, ct);

            if (steps != null && steps.Any(s => !s.IsDeleted && !s.IsValidated))
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    { "Steps", new[] { "Cannot activate PowerFlow with incomplete step configurations." } }
                });
            }

            // Build a fresh target-tenant scope factory for cross-tenant step validation
            Func<long, Task<TargetTenantRepos>> targetScopeFactory = async (targetTenantId) =>
            {
                var scope = _serviceProvider.CreateAsyncScope();
                try
                {
                    var scopedContext = scope.ServiceProvider.GetRequiredService<IQueryContext>();
                    scopedContext.SetTenantId(targetTenantId);
                    scopedContext.SetUserIdentity(
                        _queryContext.UserId,
                        _queryContext.IsSuperAdmin,
                        _queryContext.UserName,
                        _queryContext.UserEmail,
                        _queryContext.Permissions,
                        _queryContext.TenantRole);

                    var appRepo          = scope.ServiceProvider.GetRequiredService<IAppRepository>();
                    var tableRepo        = scope.ServiceProvider.GetRequiredService<IAppTableRepository>();
                    var fieldRepo        = scope.ServiceProvider.GetRequiredService<IAppFieldRepository>();
                    var appAccessService = scope.ServiceProvider.GetRequiredService<IAppAccessService>();

                    return new TargetTenantRepos(appRepo, tableRepo, fieldRepo, appAccessService, scope);
                }
                catch
                {
                    await scope.DisposeAsync();
                    throw;
                }
            };

            var stepValidator = new PipelineStepValidator(
                _pipelineRepo, _appRepo, _tableRepo, _fieldRepo,
                _appAccessService, _tenantRepo, _queryContext,
                targetScopeFactory,
                // Optional: absent means no saved-account support (see SavePipelineStepsCommandHandler).
                _serviceProvider.GetService<Connections.Common.ConnectionScopeResolver>(),
                _serviceProvider.GetService<IServiceScopeFactory>());

            foreach (var step in steps.Where(s => !s.IsDeleted && s.Type == "trigger" && s.Subtype == "new-event"))
            {
                await stepValidator.ValidateNewEventStepAsync(step.ConfigJson ?? string.Empty, ct);
            }
        }

        if (!string.Equals(pipeline.Name, command.Name, StringComparison.OrdinalIgnoreCase))
        {
            if (await _pipelineRepo.NameExistsForUserAsync(_queryContext.UserId, command.Name, ct))
                throw new DuplicateException("PowerFlow", "name", command.Name);
        }

        // Optimistic concurrency check (Dapper update checks rowversion)
        pipeline.Name = command.Name;
        pipeline.Description = command.Description;
        pipeline.IsActive = command.IsActive;
        pipeline.ModifiedOn = DateTime.UtcNow;
        pipeline.ModifiedBy = _queryContext.UserId;
        pipeline.RowVersion = command.RowVersion;

        var affected = await _pipelineRepo.UpdateAsync(pipeline, null, ct);
        if (affected == 0)
        {
            throw new ConcurrencyException("Pipeline config has been modified by another process. Please reload and try again.");
        }

        var logger = _serviceProvider.GetService<ILogger<UpdatePipelineCommandHandler>>();
        logger?.LogInformation("Pipeline {PipelineId} (PublicId: {PublicId}) successfully updated. Active: {IsActive}", pipeline.Id, pipeline.PublicId, pipeline.IsActive);

        if (isTransitioningToActive)
        {
            var steps = await _pipelineRepo.GetStepsByPipelineIdAsync(pipeline.Id, ct);
            var activeSteps = steps.Where(s => !s.IsDeleted).ToList();
            var firstStep = activeSteps
                .Where(s => string.IsNullOrEmpty(s.ParentBranch))
                .OrderBy(s => s.DisplayOrder)
                .FirstOrDefault();

            bool isSearchRecordsFirst = firstStep != null && (firstStep.Subtype == "search-records" || firstStep.Subtype == "look-up-record");

            if (isSearchRecordsFirst)
            {
                var queue = _serviceProvider.GetService<IPipelineExecutionQueue>();
                if (queue != null)
                {
                    var correlationId = Guid.NewGuid().ToString();
                    var messageId = Guid.NewGuid();

                    var task = new PipelineExecutionTask
                    {
                        TenantId = _queryContext.TenantId,
                        PipelineId = pipeline.Id,
                        TriggerEvent = "activation",
                        TriggerPayloadJson = JsonSerializer.Serialize(new
                        {
                            TriggerTime = DateTime.UtcNow,
                            ScheduledTime = DateTime.UtcNow,
                            Source = "activation"
                        }),
                        TriggeredBy = _queryContext.UserId,
                        VariablesJson = null,
                        CorrelationId = correlationId,
                        Depth = 1,
                        MessageId = messageId.ToString()
                    };

                    logger?.LogInformation("Pipeline {PipelineId} transitioned Inactive -> Active and starts with Search Records. Enqueuing immediate first run task (MessageId: {MessageId}).", pipeline.Id, messageId);
                    queue.QueueTask(task);
                }
                else
                {
                    logger?.LogWarning("IPipelineExecutionQueue not resolved during pipeline activation. Skipped immediate run enqueuing.");
                }
            }
        }

        await _auditRepo.LogActivityAsync(
            AuditActions.Updated,
            AuditEntityTypes.Pipeline,
            pipeline.PublicId.ToString(),
            $"Pipeline workflow updated: {command.Name} (Active: {command.IsActive})",
            appId: pipeline.AppId,
            ct: ct);
    }
}
