using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NCrontab;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pipelines.Commands.UpdatePipelineSchedule;

public class UpdatePipelineScheduleCommandHandler
{
    private readonly IPipelineRepository _pipelineRepo;
    private readonly IQueryContext _queryContext;

    public UpdatePipelineScheduleCommandHandler(IPipelineRepository pipelineRepo, IQueryContext queryContext)
    {
        _pipelineRepo = pipelineRepo;
        _queryContext = queryContext;
    }

    public async Task HandleAsync(UpdatePipelineScheduleCommand command, CancellationToken ct)
    {
        var validator = new UpdatePipelineScheduleCommandValidator();
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(
                validation.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        var pipelineId = await _pipelineRepo.GetIdByPublicIdAsync(command.PipelinePublicId, ct);

        var steps = await _pipelineRepo.GetStepsByPipelineIdAsync(pipelineId, ct);
        if (!PipelineScheduleEligibility.IsPipelineScheduleable(steps))
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                { "Pipeline", new[] { "PowerFlow schedule is only allowed when the first step is an executable Action or Query step, and no trigger step exists." } }
            });
        }

        var schedule = await _pipelineRepo.GetScheduleByPipelineIdAsync(pipelineId, ct);

        var dummySchedule = new PipelineSchedule
        {
            ScheduleType = command.ScheduleType,
            Interval = command.Interval,
            TimeOfDay = command.TimeOfDay,
            Weekdays = command.Weekdays,
            MonthDay = command.MonthDay,
            MonthOfYear = command.MonthOfYear,
            RelativeWeek = command.RelativeWeek,
            RelativeDay = command.RelativeDay,
            TimeZone = command.TimeZone,
            CronExpression = command.CronExpression,
            CreatedOn = schedule?.CreatedOn ?? DateTime.UtcNow
        };
        var nextRunUtc = ScheduleNextRunCalculator.CalculateNextRun(dummySchedule, DateTime.UtcNow);

        var pipeline = await _pipelineRepo.GetByPublicIdAsync(command.PipelinePublicId, ct);
        bool scheduleChanged = schedule == null ||
                               schedule.ScheduleType != command.ScheduleType ||
                               schedule.Interval != command.Interval ||
                               schedule.TimeOfDay != command.TimeOfDay ||
                               schedule.Weekdays != command.Weekdays ||
                               schedule.MonthDay != command.MonthDay ||
                               schedule.MonthOfYear != command.MonthOfYear ||
                               schedule.RelativeWeek != command.RelativeWeek ||
                               schedule.RelativeDay != command.RelativeDay ||
                               schedule.TimeZone != command.TimeZone ||
                               schedule.CronExpression != command.CronExpression;

        bool shouldDeactivate = pipeline.IsActive && scheduleChanged;

        if (schedule == null)
        {
            schedule = new PipelineSchedule
            {
                PipelineId = pipelineId,
                ScheduleType = command.ScheduleType,
                Interval = command.Interval,
                TimeOfDay = command.TimeOfDay,
                Weekdays = command.Weekdays,
                MonthDay = command.MonthDay,
                MonthOfYear = command.MonthOfYear,
                RelativeWeek = command.RelativeWeek,
                RelativeDay = command.RelativeDay,
                TimeZone = command.TimeZone,
                CronExpression = command.CronExpression,
                NextRunOn = nextRunUtc,
                LastRunOn = null
            };
            await _pipelineRepo.CreateScheduleAsync(schedule, transaction: null, ct);
        }
        else
        {
            schedule.ScheduleType = command.ScheduleType;
            schedule.Interval = command.Interval;
            schedule.TimeOfDay = command.TimeOfDay;
            schedule.Weekdays = command.Weekdays;
            schedule.MonthDay = command.MonthDay;
            schedule.MonthOfYear = command.MonthOfYear;
            schedule.RelativeWeek = command.RelativeWeek;
            schedule.RelativeDay = command.RelativeDay;
            schedule.TimeZone = command.TimeZone;
            schedule.CronExpression = command.CronExpression;
            schedule.NextRunOn = nextRunUtc;
            await _pipelineRepo.UpdateScheduleAsync(schedule, transaction: null, ct);
        }

        // Auto-activate if all steps are successfully validated, otherwise deactivate
        bool isAllStepsValidated = steps.Where(s => !s.IsDeleted).All(s => s.IsValidated);
        if (pipeline.IsActive != isAllStepsValidated)
        {
            pipeline.IsActive = isAllStepsValidated;
            pipeline.ModifiedOn = DateTime.UtcNow;
            pipeline.ModifiedBy = _queryContext.UserId;
            await _pipelineRepo.UpdateAsync(pipeline, null, ct);
        }
    }
}
