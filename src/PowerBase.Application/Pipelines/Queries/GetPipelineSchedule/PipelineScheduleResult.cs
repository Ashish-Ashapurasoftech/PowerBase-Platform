using System;

namespace PowerBase.Application.Pipelines.Queries.GetPipelineSchedule;

public class PipelineScheduleResult
{
    public Guid PublicId { get; set; }
    public Guid PipelinePublicId { get; set; }
    public string ScheduleType { get; set; } = string.Empty;
    public int? Interval { get; set; }
    public TimeSpan? TimeOfDay { get; set; }
    public string? Weekdays { get; set; }
    public string? MonthDay { get; set; }
    public int? MonthOfYear { get; set; }
    public int? RelativeWeek { get; set; }
    public int? RelativeDay { get; set; }
    public string TimeZone { get; set; } = "UTC";
    public string CronExpression { get; set; } = string.Empty;
    public DateTime? NextRunOn { get; set; }
    public DateTime? LastRunOn { get; set; }
}
