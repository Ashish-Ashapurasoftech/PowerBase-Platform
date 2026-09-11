using FluentValidation;
using NCrontab;
using System;

namespace PowerBase.Application.Pipelines.Commands.UpdatePipelineSchedule;

public class UpdatePipelineScheduleCommandValidator : AbstractValidator<UpdatePipelineScheduleCommand>
{
    public UpdatePipelineScheduleCommandValidator()
    {
        RuleFor(x => x.PipelinePublicId).NotEmpty();
        RuleFor(x => x.ScheduleType).NotEmpty().Must(x => 
            x == "hourly" || x == "daily" || x == "weekly" || x == "monthly" || x == "yearly" || x == "custom");
        RuleFor(x => x.TimeZone).NotEmpty();
        RuleFor(x => x.CronExpression)
            .NotEmpty()
            .When(x => x.ScheduleType == "custom" || x.ScheduleType == "hourly")
            .Must(IsValidCron).WithMessage("Invalid cron expression format. Must contain exactly 5 fields and no aliases.")
            .Must(IsAtLeastHourly).WithMessage("Minimum schedule frequency is 1 hour. Minute field must specify a single integer (0-59).");
        
        RuleFor(x => x.Interval)
            .Must((cmd, val) => cmd.ScheduleType != "hourly" || val == null || val >= 1)
            .WithMessage("Interval must be at least 1 hour.");
    }

    private bool IsValidCron(string cron)
    {
        if (string.IsNullOrWhiteSpace(cron)) return false;
        if (cron.StartsWith("@")) return false;
        var parts = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) return false;
        try
        {
            CrontabSchedule.Parse(cron);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool IsAtLeastHourly(string cron)
    {
        if (string.IsNullOrWhiteSpace(cron)) return false;
        var parts = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) return false;

        var minuteField = parts[0];
        return int.TryParse(minuteField, out var min) && min >= 0 && min <= 59;
    }
}
