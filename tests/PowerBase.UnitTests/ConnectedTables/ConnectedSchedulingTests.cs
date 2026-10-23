using FluentAssertions;
using PowerBase.Application.ConnectedTables.Scheduling;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.ConnectedTables;

public class ConnectedSchedulingTests
{
    private static ConnectedSchedule S(string type, int hour = 9, int minute = 15, int day = 1, int weekday = 0, int endHour = 0) =>
        new() { Type = type, Hour = hour, Minute = minute, Day = day, Weekday = weekday, EndHour = endHour, Enabled = true };

    [Fact]
    public void NextRun_Manual_IsNull()
    {
        ConnectedScheduling.NextRun(S("manual"), "UTC", DateTime.UtcNow).Should().BeNull();
    }

    [Fact]
    public void NextRun_Paused_IsNull()
    {
        var s = S("daily");
        s.Enabled = false;
        ConnectedScheduling.NextRun(s, "UTC", DateTime.UtcNow).Should().BeNull();
    }

    [Fact]
    public void NextRun_Daily_IsNextOccurrence()
    {
        var after = new DateTime(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc);
        ConnectedScheduling.NextRun(S("daily", hour: 9), "UTC", after).Should().Be(new DateTime(2026, 10, 23, 9, 15, 0));
        ConnectedScheduling.NextRun(S("daily", hour: 11), "UTC", after).Should().Be(new DateTime(2026, 10, 22, 11, 15, 0));
    }

    [Fact]
    public void NextRun_Weekly_LandsOnWeekday()
    {
        var after = new DateTime(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc); // Thursday
        var next = ConnectedScheduling.NextRun(S("weekly", hour: 1, weekday: 0), "UTC", after)!.Value;
        next.DayOfWeek.Should().Be(DayOfWeek.Sunday);
        next.Should().Be(new DateTime(2026, 10, 25, 1, 15, 0));
    }

    [Fact]
    public void NextRun_Monthly_ShortMonth_RunsOnLastDay()
    {
        var after = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        ConnectedScheduling.NextRun(S("monthly", hour: 0, day: 31), "UTC", after).Should().Be(new DateTime(2026, 2, 28, 0, 15, 0));
    }

    [Fact]
    public void NextRun_Hourly_StaysInsideWindow()
    {
        var after = new DateTime(2026, 10, 22, 22, 30, 0, DateTimeKind.Utc);
        ConnectedScheduling.NextRun(S("hourly", hour: 8, endHour: 18, minute: 5), "UTC", after).Should().Be(new DateTime(2026, 10, 23, 8, 5, 0));
        var inside = new DateTime(2026, 10, 22, 12, 30, 0, DateTimeKind.Utc);
        ConnectedScheduling.NextRun(S("hourly", hour: 8, endHour: 18, minute: 5), "UTC", inside).Should().Be(new DateTime(2026, 10, 22, 13, 5, 0));
    }

    [Theory]
    [InlineData(0, 0, 13, true)]
    [InlineData(8, 18, 19, false)]
    [InlineData(22, 2, 23, true)]
    [InlineData(22, 2, 1, true)]
    [InlineData(22, 2, 12, false)]
    public void InHourlyWindow_HandlesWholeDayAndWrap(int start, int end, int hour, bool expected)
    {
        ConnectedScheduling.InHourlyWindow(start, end, hour).Should().Be(expected);
    }

    [Fact]
    public void NextRun_UsesTheTimeZone()
    {
        if (!ConnectedScheduling.TryResolveTimeZone("Asia/Kolkata", out _)) return; // zone database not available on this machine
        var after = new DateTime(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc);
        // 09:15 in India (UTC+5:30) is 03:45 UTC.
        ConnectedScheduling.NextRun(S("daily", hour: 9), "Asia/Kolkata", after).Should().Be(new DateTime(2026, 10, 22, 3, 45, 0));
    }

    [Fact]
    public void Validate_NewShape_GetsRandomMinute_SameShape_KeepsIt()
    {
        var first = ConnectedScheduling.Validate(S("daily", hour: 5, minute: 0), null, new Random(1));
        first.Minute.Should().BeInRange(0, 59);
        var again = ConnectedScheduling.Validate(S("daily", hour: 5, minute: 0), first, new Random(2));
        again.Minute.Should().Be(first.Minute);
    }

    [Fact]
    public void Validate_BadValues_Throw()
    {
        FluentActions.Invoking(() => ConnectedScheduling.Validate(S("yearly"), null)).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => ConnectedScheduling.Validate(S("daily", hour: 24), null)).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => ConnectedScheduling.Validate(S("monthly", day: 0), null)).Should().Throw<ValidationException>();
    }

    [Fact]
    public void Describe_ReadsLikeTheWizard()
    {
        ConnectedScheduling.Describe(null, "UTC").Should().Be("Manual only");
        ConnectedScheduling.Describe(S("daily", hour: 0), "UTC").Should().Be("Every day between 12 AM and 1 AM (UTC)");
    }
}
