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

    public UpdatePipelineScheduleCommandHandler(IPipelineRepository pipelineRepo)
    {
        _pipelineRepo = pipelineRepo;
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
        var firstStep = steps.Where(s => s.ParentStepId == null).OrderBy(s => s.DisplayOrder).FirstOrDefault();
        if (firstStep == null || firstStep.Type != "query" || (firstStep.Subtype != "search-records" && firstStep.Subtype != "look-up-record"))
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                { "Pipeline", new[] { "PowerFlow schedule is only allowed when the first step is a Search Records or Look Up a Record (Query) step." } }
            });
        }

        var schedule = await _pipelineRepo.GetScheduleByPipelineIdAsync(pipelineId, ct);

        // Resolve timezone info for NextRun calculation
        TimeZoneInfo timeZoneInfo;
        try
        {
            timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(command.TimeZone);
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
            if (map.TryGetValue(command.TimeZone, out var winId))
            {
                try { timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(winId); }
                catch { timeZoneInfo = TimeZoneInfo.Utc; }
            }
            else
            {
                timeZoneInfo = TimeZoneInfo.Utc;
            }
        }

        // Calculate next run time strictly from local now
        var cron = CrontabSchedule.Parse(command.CronExpression);
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZoneInfo);
        var nextRunLocal = cron.GetNextOccurrence(nowLocal);
        var nextRunUtc = TimeZoneInfo.ConvertTimeToUtc(nextRunLocal, timeZoneInfo);

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
    }
}
