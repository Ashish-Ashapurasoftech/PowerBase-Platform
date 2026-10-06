using PowerBase.Application.Pipelines;

namespace PowerBase.UnitTests.Pipelines;

public class RelativeFilterDateTests
{
    [Theory]
    [InlineData("today", 0, "2026-10-04T18:30:00.0000000Z")]
    [InlineData("yesterday", 0, "2026-10-03T18:30:00.0000000Z")]
    [InlineData("tomorrow", 0, "2026-10-05T18:30:00.0000000Z")]
    [InlineData("past", 7, "2026-09-27T18:30:00.0000000Z")]
    [InlineData("future", 30, "2026-11-03T18:30:00.0000000Z")]
    public void ResolvesCalendarDateInSavedTimezone(string mode, int days, string expected)
    {
        Assert.Equal(expected, RelativeFilterDate.Resolve($"@relative-date|{mode}|{days}|Asia/Kolkata", DateTimeOffset.Parse("2026-10-05T10:00:00Z")));
    }

    [Theory]
    [InlineData("2026-03-08T12:00:00Z", 23)]
    [InlineData("2026-11-01T12:00:00Z", 25)]
    public void CalendarWindowAccountsForDaylightSaving(string now, int hours)
    {
        var window = RelativeFilterDate.Window("@relative-date|today|0|America/New_York", DateTimeOffset.Parse(now));
        Assert.Equal(hours, (window.End - window.Start).TotalHours);
    }

    [Fact]
    public void SameSavedRuleMovesOnNextRun()
    {
        const string rule = "@relative-date|today|0|Asia/Kolkata";
        var now = DateTimeOffset.Parse("2026-10-05T10:00:00Z");
        Assert.Equal(DateTime.Parse(RelativeFilterDate.Resolve(rule, now)).AddDays(1), DateTime.Parse(RelativeFilterDate.Resolve(rule, now.AddDays(1))));
    }

    [Theory]
    [InlineData("@relative-date|past|-1|UTC")]
    [InlineData("@relative-date|future|1.5|UTC")]
    [InlineData("@relative-date|unknown|0|UTC")]
    public void RejectsMalformedRules(string rule) => Assert.Throws<ArgumentException>(() => RelativeFilterDate.Resolve(rule));

    [Fact]
    public void RuntimeEvaluationMatchesTodayButDoesNotInterpretTextFields()
    {
        const string rule = "@relative-date|today|0|UTC";
        Assert.True(PipelineFilterEvaluator.EvaluateConditionOperator(DateTime.UtcNow.ToString("O"), "is", rule, "DATE"));
        Assert.False(PipelineFilterEvaluator.EvaluateConditionOperator(DateTime.UtcNow.ToString("O"), "is", rule, "TEXT"));
    }
}
