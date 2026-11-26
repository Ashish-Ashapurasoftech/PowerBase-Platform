using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PowerBase.Application.Common.Configurations;
using PowerBase.Infrastructure.Services;
using Xunit;

namespace PowerBase.UnitTests.Common;

/// <summary>Doc 21: heavy reads per tenant DB are limited per instance and slowed under high Azure SQL load — never failed.</summary>
public class HeavyReadGovernorTests
{
    private static HeavyReadGovernor Governor(SqlWorkloadOptions o) => new(Options.Create(o), NullLogger<HeavyReadGovernor>.Instance);

    [Fact]
    public async Task At_most_the_configured_number_of_heavy_reads_run_at_once_per_tenant_db()
    {
        var g = Governor(new SqlWorkloadOptions { HeavyReadsPerTenantDb = 2 });
        var inside = 0;
        var maxInside = 0;
        async Task Read()
        {
            using var slot = await g.EnterAsync(45, _ => Task.FromResult<double?>(10), default);
            var now = Interlocked.Increment(ref inside);
            lock (this) maxInside = Math.Max(maxInside, now);
            await Task.Delay(50);
            Interlocked.Decrement(ref inside);
        }
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(Read)));
        maxInside.Should().Be(2);
    }

    [Fact]
    public async Task Another_tenant_db_has_its_own_slots()
    {
        var g = Governor(new SqlWorkloadOptions { HeavyReadsPerTenantDb = 1 });
        using var first = await g.EnterAsync(45, _ => Task.FromResult<double?>(null), default);
        var other = g.EnterAsync(46, _ => Task.FromResult<double?>(null), default);
        (await Task.WhenAny(other, Task.Delay(1000))).Should().BeSameAs(other);
        (await other).Dispose();
    }

    [Theory]
    [InlineData(85, 150)]   // slow: SlowDelayMs
    [InlineData(97, 400)]   // stop: StopDelayMs
    public async Task High_load_makes_each_batch_wait_first(double load, int expectedMs)
    {
        var g = Governor(new SqlWorkloadOptions { SlowDelayMs = 150, StopDelayMs = 400 });
        var sw = Stopwatch.StartNew();
        using (await g.EnterAsync(45, _ => Task.FromResult<double?>(load), default)) { }
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(expectedMs - 20);
    }

    [Fact]
    public async Task Without_a_load_signal_it_limits_by_count_only_and_stops_asking()
    {
        var g = Governor(new SqlWorkloadOptions());
        var probes = 0;
        Task<double?> Failing(CancellationToken _) { probes++; throw new InvalidOperationException("Invalid object name 'sys.dm_db_resource_stats'."); }
        for (var i = 0; i < 3; i++)
            using (await g.EnterAsync(45, Failing, default)) { }
        probes.Should().Be(1);
    }

    [Fact]
    public async Task Switched_off_it_does_nothing()
    {
        var g = Governor(new SqlWorkloadOptions { Enabled = false, HeavyReadsPerTenantDb = 1 });
        using var a = await g.EnterAsync(45, _ => Task.FromResult<double?>(99), default);
        var b = g.EnterAsync(45, _ => Task.FromResult<double?>(99), default);
        b.IsCompleted.Should().BeTrue();
        (await b).Dispose();
    }
}
