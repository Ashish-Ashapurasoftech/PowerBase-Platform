using FluentAssertions;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Infrastructure.Services;
using Xunit;

namespace PowerBase.UnitTests.Common;

public class ServerTimingTests
{
    [Fact]
    public void Header_lists_every_measured_step_in_order()
    {
        var timing = new ServerTiming();
        using (timing.Measure("rows")) { }
        timing.Add("count", TimeSpan.FromMilliseconds(12.34));

        var header = timing.ToHeaderValue();

        header.Should().StartWith("rows;dur=");
        header.Should().EndWith("count;dur=12.3");
        header.Should().Contain(", ");
    }

    [Fact]
    public void Header_is_empty_when_nothing_was_measured()
    {
        new ServerTiming().ToHeaderValue().Should().BeEmpty();
    }

    [Fact]
    public void Measure_records_the_time_the_scope_was_open()
    {
        var timing = new ServerTiming();
        using (timing.Measure("slow")) Thread.Sleep(30);

        var ms = double.Parse(timing.ToHeaderValue().Split("dur=")[1], System.Globalization.CultureInfo.InvariantCulture);
        ms.Should().BeGreaterThanOrEqualTo(25);
    }

    [Fact]
    public void Steps_added_from_parallel_tasks_are_all_kept()
    {
        var timing = new ServerTiming();
        Parallel.For(0, 50, i => timing.Add($"s{i}", TimeSpan.FromMilliseconds(1)));

        timing.ToHeaderValue().Split(", ").Should().HaveCount(50);
    }

    [Fact]
    public void NoServerTiming_accepts_calls_and_records_nothing()
    {
        var timing = NoServerTiming.Instance;
        using (timing.Measure("x")) { }
        timing.Add("y", TimeSpan.FromSeconds(1));
    }
}
