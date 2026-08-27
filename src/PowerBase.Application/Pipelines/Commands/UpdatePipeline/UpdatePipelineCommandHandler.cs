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
    private readonly IMainPipelineQueueRepository _queueRepo;
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
        IMainPipelineQueueRepository queueRepo,
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
        _queueRepo = queueRepo;
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
        bool isTransitioningToInactive = pipeline.IsActive && !command.IsActive;

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

            foreach (var step in steps.Where(s => !s.IsDeleted && s.Type == "trigger" && (s.Subtype == "new-event" || s.Subtype == "new-bulk-event")))
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

        var sentinelDate = new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

        if (isTransitioningToActive)
        {
            // Resume sentinel-paused jobs in the Control DB
            var resumedJobs = await _queueRepo.ResumePendingJobsAsync(_queryContext.TenantId, pipeline.Id, sentinelDate, ct);
            logger?.LogInformation("Pipeline {PipelineId} reactivated. Resumed {ResumedCount} sentinel-paused jobs in queue.", pipeline.Id, resumedJobs);

            var schedule = await _pipelineRepo.GetScheduleByPipelineIdAsync(pipeline.Id, ct);
            if (schedule != null)
            {
                TimeZoneInfo timeZoneInfo;
                try
                {
                    timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZone);
                }
                catch
                {
                    var map = new System.Collections.Generic.Dictionary<string, string>
                    {
                        { "America/New_York", "Eastern Standard Time" },
                        { "America/Chicago", "Central Standard Time" },
                        { "America/Denver", "Mountain Standard Time" },
                        { "America/Los_Angeles", "Pacific Standard Time" },
                        { "Asia/Kolkata", "India Standard Time" },
                        { "UTC", "UTC" }
                    };
                    if (map.TryGetValue(schedule.TimeZone, out var winId))
                    {
                        try { timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(winId); }
                        catch { timeZoneInfo = TimeZoneInfo.Utc; }
                    }
                    else
                    {
                        timeZoneInfo = TimeZoneInfo.Utc;
                    }
                }

                var cron = NCrontab.CrontabSchedule.Parse(schedule.CronExpression);
                var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZoneInfo);
                var nextRunLocal = cron.GetNextOccurrence(nowLocal);
                var nextRunUtc = TimeZoneInfo.ConvertTimeToUtc(nextRunLocal, timeZoneInfo);
                schedule.NextRunOn = nextRunUtc;
                await _pipelineRepo.UpdateScheduleAsync(schedule, transaction: null, ct);
                logger?.LogInformation("Pipeline {PipelineId} schedule NextRunOn recalculated on reactivation: {NextRunOn}", pipeline.Id, nextRunUtc);
            }
        }
        else if (isTransitioningToInactive)
        {
            // Pause pending jobs in the Control DB using sentinel date
            var pausedJobs = await _queueRepo.PausePendingJobsAsync(_queryContext.TenantId, pipeline.Id, sentinelDate, ct);
            logger?.LogInformation("Pipeline {PipelineId} deactivated. Paused {PausedCount} pending jobs in queue with sentinel date.", pipeline.Id, pausedJobs);
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
