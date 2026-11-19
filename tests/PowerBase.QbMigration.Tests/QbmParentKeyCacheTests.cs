using FluentAssertions;
using PowerBase.Infrastructure.QbMigration;
using static PowerBase.QbMigration.Tests.TestData;

namespace PowerBase.QbMigration.Tests;

/// <summary>A child whose reference is matched to its parent by a text key (Goals In Session -> Goals "Goal ID") must not scan the
/// parent table for every chunk: on CFTSS one 1,000-key batch took 45 s against a 307,000-row parent and every chunk timed out.
/// The parent's keys are read once per worker process and shared.</summary>
public class QbmParentKeyCacheTests
{
    private static IReadOnlyDictionary<string, long> Keys(params (string Key, long Id)[] rows) => rows.ToDictionary(r => r.Key, r => r.Id);

    [Fact]
    public async Task A_parents_keys_are_read_once_however_many_chunks_ask()
    {
        var reads = 0;
        var cache = new QbmParentKeyCache(async (table, key, tenant, app, ct) =>
        {
            Interlocked.Increment(ref reads);
            await Task.Delay(30, ct); // concurrent chunks arrive while the first read is still running
            return Keys(("G-1", 10), ("G-2", 11));
        });
        var key = Pb(6, "Text", "Goal ID");
        var maps = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => cache.GetAsync(288, key, 1, 5)));
        reads.Should().Be(1);
        maps.Should().OnlyContain(m => m["G-1"] == 10 && m["G-2"] == 11);
    }

    [Fact]
    public async Task Each_parent_table_and_key_field_has_its_own_map()
    {
        var reads = new List<(long Table, int Fid)>();
        var cache = new QbmParentKeyCache((table, key, tenant, app, ct) =>
        {
            reads.Add((table, key.Fid!.Value));
            return Task.FromResult(Keys(($"k{table}-{key.Fid}", table)));
        });
        (await cache.GetAsync(288, Pb(6, "Text"), 1, 5)).Keys.Should().Equal("k288-6");
        (await cache.GetAsync(289, Pb(6, "Text"), 1, 5)).Keys.Should().Equal("k289-6");
        (await cache.GetAsync(288, Pb(7, "Text"), 1, 5)).Keys.Should().Equal("k288-7");
        (await cache.GetAsync(288, Pb(6, "Text"), 1, 5)).Keys.Should().Equal("k288-6");
        reads.Should().Equal((288, 6), (289, 6), (288, 7));
    }

    [Fact]
    public async Task A_failed_read_is_not_kept_so_the_retry_reads_again()
    {
        var reads = 0;
        var cache = new QbmParentKeyCache((table, key, tenant, app, ct) =>
            ++reads == 1 ? throw new InvalidOperationException("timeout") : Task.FromResult(Keys(("G-1", 10))));
        var key = Pb(6, "Text");
        await FluentActions.Awaiting(() => cache.GetAsync(288, key, 1, 5)).Should().ThrowAsync<InvalidOperationException>();
        (await cache.GetAsync(288, key, 1, 5))["G-1"].Should().Be(10);
        reads.Should().Be(2);
    }
}
