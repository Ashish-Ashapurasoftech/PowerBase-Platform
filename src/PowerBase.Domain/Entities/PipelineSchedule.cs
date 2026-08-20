using System;

namespace PowerBase.Domain.Entities;

public class PipelineSchedule
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long PipelineId { get; set; }
    public string ScheduleType { get; set; } = string.Empty; // 'hourly', 'daily', 'weekly', 'monthly', 'yearly', 'custom'
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
    public bool IsDeleted { get; set; }
    public DateTime CreatedOn { get; set; }
    public long CreatedBy { get; set; }
    public DateTime? ModifiedOn { get; set; }
    public long? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
