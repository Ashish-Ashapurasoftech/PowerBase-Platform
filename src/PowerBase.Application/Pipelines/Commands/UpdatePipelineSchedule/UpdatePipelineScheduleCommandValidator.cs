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
        RuleFor(x => x.CronExpression).NotEmpty().Must(IsValidCron)
            .WithMessage("Invalid cron expression format. Must contain 5 fields.");
    }

    private bool IsValidCron(string cron)
    {
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
}
