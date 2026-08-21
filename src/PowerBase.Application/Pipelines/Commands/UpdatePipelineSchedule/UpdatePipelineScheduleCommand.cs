using System;

namespace PowerBase.Application.Pipelines.Commands.UpdatePipelineSchedule;

public class UpdatePipelineScheduleCommand
{
    public Guid PipelinePublicId { get; }
    public string ScheduleType { get; }
    public int? Interval { get; }
    public TimeSpan? TimeOfDay { get; }
    public string? Weekdays { get; }
    public string? MonthDay { get; }
    public int? MonthOfYear { get; }
    public int? RelativeWeek { get; }
    public int? RelativeDay { get; }
    public string TimeZone { get; }
    public string CronExpression { get; }

    public UpdatePipelineScheduleCommand(
        Guid pipelinePublicId,
        string scheduleType,
        int? interval,
        TimeSpan? timeOfDay,
        string? weekdays,
        string? monthDay,
        int? monthOfYear,
        int? relativeWeek,
        int? relativeDay,
        string timeZone,
        string cronExpression)
    {
        PipelinePublicId = pipelinePublicId;
        ScheduleType = scheduleType;
        Interval = interval;
        TimeOfDay = timeOfDay;
        Weekdays = weekdays;
        MonthDay = monthDay;
        MonthOfYear = monthOfYear;
        RelativeWeek = relativeWeek;
        RelativeDay = relativeDay;
        TimeZone = timeZone;
        CronExpression = cronExpression;
    }
}
