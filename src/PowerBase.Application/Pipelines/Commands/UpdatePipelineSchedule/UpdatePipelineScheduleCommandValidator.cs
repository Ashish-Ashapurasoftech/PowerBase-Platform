using FluentValidation;
using NCrontab;
using System;
using System.Linq;

namespace PowerBase.Application.Pipelines.Commands.UpdatePipelineSchedule;

public class UpdatePipelineScheduleCommandValidator : AbstractValidator<UpdatePipelineScheduleCommand>
{
    public UpdatePipelineScheduleCommandValidator()
    {
        RuleFor(x => x.PipelinePublicId).NotEmpty();
        RuleFor(x => x.ScheduleType).NotEmpty().Must(x =>
            x == "hourly" || x == "daily" || x == "weekly" || x == "monthly" || x == "yearly" || x == "custom");
        RuleFor(x => x.TimeZone)
            .NotEmpty()
            .Must(x => ScheduleNextRunCalculator.TryResolveTimeZone(x, out _))
            .WithMessage("Time zone is not recognized.");
        RuleFor(x => x.CronExpression)
            .NotEmpty()
            .Must(IsValidCron).WithMessage("Invalid cron expression format. Must contain exactly 5 fields and no aliases.")
            .Must(IsAtLeastHourly).WithMessage("Minimum schedule frequency is 1 hour. Minute field must specify a single integer (0-59).")
            .When(x => x.ScheduleType == "custom");

        RuleFor(x => x.Interval)
            .Must((command, value) => command.ScheduleType == "custom" || value == null || value >= 1)
            .WithMessage("Interval must be at least 1.");

        RuleFor(x => x.TimeOfDay)
            .NotNull()
            .When(x => x.ScheduleType is "daily" or "weekly" or "monthly" or "yearly");

        RuleFor(x => x.Weekdays)
            .Must(HasValidWeekdays)
            .When(x => x.ScheduleType == "weekly")
            .WithMessage("Select at least one valid weekday.");

        RuleFor(x => x)
            .Must(HasValidMonthlySelection)
            .When(x => x.ScheduleType == "monthly")
            .OverridePropertyName("MonthDay")
            .WithMessage("Select valid days of the month or a valid relative weekday.");

        RuleFor(x => x)
            .Must(HasValidYearlySelection)
            .When(x => x.ScheduleType == "yearly")
            .OverridePropertyName("MonthDay")
            .WithMessage("Select a valid month and day.");
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

    private static bool HasValidWeekdays(string? weekdays)
    {
        if (string.IsNullOrWhiteSpace(weekdays)) return false;
        var tokens = weekdays.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length > 0 && tokens.All(token => int.TryParse(token, out var day) && day >= 0 && day <= 6);
    }

    private static bool HasValidMonthlySelection(UpdatePipelineScheduleCommand command)
    {
        var usesRelativeDate = command.RelativeWeek.HasValue || command.RelativeDay.HasValue;
        if (usesRelativeDate)
        {
            return command.RelativeWeek is >= 1 and <= 5 && command.RelativeDay is >= 0 and <= 6;
        }

        return HasValidMonthDays(command.MonthDay, allowMultiple: true);
    }

    private static bool HasValidYearlySelection(UpdatePipelineScheduleCommand command)
    {
        if (command.MonthOfYear is not (>= 1 and <= 12) ||
            !HasValidMonthDays(command.MonthDay, allowMultiple: false))
        {
            return false;
        }

        var token = command.MonthDay!.Trim();
        return string.Equals(token, "last", StringComparison.OrdinalIgnoreCase) ||
               (int.TryParse(token, out var day) && day <= DateTime.DaysInMonth(2000, command.MonthOfYear.Value));
    }

    private static bool HasValidMonthDays(string? monthDays, bool allowMultiple)
    {
        if (string.IsNullOrWhiteSpace(monthDays)) return false;
        var tokens = monthDays.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0 || (!allowMultiple && tokens.Length != 1)) return false;

        return tokens.All(token => string.Equals(token, "last", StringComparison.OrdinalIgnoreCase) ||
                                   (int.TryParse(token, out var day) && day >= 1 && day <= 31));
    }
}
