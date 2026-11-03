using System;
using FluentAssertions;
using PowerBase.Application.Import.Qbl;
using PowerBase.Application.Pipelines.Commands.UpdatePipelineSchedule;
using Xunit;

namespace PowerBase.UnitTests.Import.Qbl;

public class QblCrontabScheduleTests
{
    [Theory]
    // Crontabs from the real exports (Pipeline_New.qbl, PowerFlow100Pipeline.qbl).
    [InlineData("0 3 1 * *", "monthly", "03:00", null, "1", null, "0 3 1 * *")]
    [InlineData("30 1 * * *", "daily", "01:30", null, null, null, "30 1 * * *")]
    [InlineData("0 * * * *", "hourly", null, null, null, null, "0 */1 * * *")]
    [InlineData("15 8 * * 1,3,5", "weekly", "08:15", "1,3,5", null, null, "15 8 * * 1,3,5")]
    [InlineData("0 6 * * 7,0", "weekly", "06:00", "0", null, null, "0 6 * * 0")]
    [InlineData("0 6 * * 0,1,2,3,4,5,6", "daily", "06:00", null, null, null, "0 6 * * *")]
    [InlineData("0 9 15,1 * *", "monthly", "09:00", null, "1,15", null, "0 9 1,15 * *")]
    [InlineData("0 9 25 12 *", "yearly", "09:00", null, "25", 12, "0 9 25 12 *")]
    public void From_EditorOption_MapsToThatOption(string crontab, string type, string? time, string? weekdays, string? monthDay, int? month, string cron)
    {
        var schedule = QblCrontabSchedule.From(crontab);

        schedule.ScheduleType.Should().Be(type);
        schedule.Interval.Should().Be(1);
        schedule.TimeOfDay.Should().Be(time is null ? null : TimeSpan.Parse(time));
        schedule.Weekdays.Should().Be(weekdays);
        schedule.MonthDay.Should().Be(monthDay);
        schedule.MonthOfYear.Should().Be(month);
        schedule.CronExpression.Should().Be(cron);
    }

    [Theory]
    [InlineData("45 * * * *")]               // hourly at :45 — the editor's hourly runs on the hour
    [InlineData("0 9-20 * * 0,1,2,3,4,5")]  // hour range
    [InlineData("0 */2 * * *")]              // hour step
    [InlineData("0 9 1-7 * 1")]              // day range with weekday
    [InlineData("0 9 1 1,6 *")]              // several months
    public void From_NotAnEditorOption_KeepsTheCronAsCustom(string crontab)
    {
        var schedule = QblCrontabSchedule.From(crontab);

        schedule.ScheduleType.Should().Be("custom");
        schedule.CronExpression.Should().Be(crontab);
        schedule.TimeOfDay.Should().BeNull();
    }

    [Theory]
    [InlineData("0 3 1 * *")]
    [InlineData("30 1 * * *")]
    [InlineData("0 * * * *")]
    [InlineData("15 8 * * 1,3,5")]
    [InlineData("0 9 1,15 * *")]
    [InlineData("0 9 25 12 *")]
    public void From_EditorOption_RunsAtTheSameTimesAsTheCron(string crontab)
    {
        var s = QblCrontabSchedule.From(crontab);
        var created = new DateTime(2026, 11, 3, 9, 19, 0, DateTimeKind.Utc);
        var mapped = new PowerBase.Domain.Entities.PipelineSchedule
        {
            ScheduleType = s.ScheduleType, Interval = s.Interval, TimeOfDay = s.TimeOfDay, Weekdays = s.Weekdays, MonthDay = s.MonthDay,
            MonthOfYear = s.MonthOfYear, TimeZone = "America/New_York", CronExpression = s.CronExpression, CreatedOn = created,
        };
        var cron = new PowerBase.Domain.Entities.PipelineSchedule
        {
            ScheduleType = "custom", TimeZone = "America/New_York", CronExpression = crontab, CreatedOn = created,
        };

        var from = created;
        for (var i = 0; i < 40; i++)
        {
            var expected = PowerBase.Application.Pipelines.ScheduleNextRunCalculator.CalculateNextRun(cron, from);
            PowerBase.Application.Pipelines.ScheduleNextRunCalculator.CalculateNextRun(mapped, from).Should().Be(expected, $"run {i + 1} after {from:u}");
            from = expected;
        }
    }

    [Theory]
    [InlineData("0 3 1 * *")]
    [InlineData("30 1 * * *")]
    [InlineData("0 * * * *")]
    [InlineData("15 8 * * 1,3,5")]
    [InlineData("0 9 25 12 *")]
    [InlineData("45 * * * *")]
    public void From_Result_PassesTheScheduleValidator(string crontab)
    {
        var s = QblCrontabSchedule.From(crontab);
        var command = new UpdatePipelineScheduleCommand(Guid.NewGuid(), s.ScheduleType, s.Interval, s.TimeOfDay, s.Weekdays, s.MonthDay,
            s.MonthOfYear, null, null, "America/New_York", s.CronExpression);

        new UpdatePipelineScheduleCommandValidator().Validate(command).IsValid.Should().BeTrue();
    }
}
