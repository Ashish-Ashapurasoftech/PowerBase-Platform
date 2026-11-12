using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PowerBase.Application.Common.Configurations;
using PowerBase.Infrastructure.Services;
using Xunit;

namespace PowerBase.UnitTests.Common;

public class MetadataCacheTests
{
    private static MetadataCache Create(bool enabled = true, int quarantineSeconds = 0) =>
        new(new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new MetadataCacheOptions { Enabled = enabled, WriteQuarantineSeconds = quarantineSeconds, TransactionQuarantineSeconds = quarantineSeconds }),
            NullLogger<MetadataCache>.Instance);

    [Fact]
    public async Task Second_read_comes_from_the_cache()
    {
        var cache = Create();
        var loads = 0;
        Task<string> Load() { loads++; return Task.FromResult("v"); }

        await cache.GetOrAddAsync(1, "fields", "t:1", Load);
        await cache.GetOrAddAsync(1, "fields", "t:1", Load);

        loads.Should().Be(1);
    }

    [Fact]
    public async Task Invalidate_drops_every_entry_of_the_group_only()
    {
        var cache = Create();
        var loads = 0;
        Task<string> Load() { loads++; return Task.FromResult("v"); }
        await cache.GetOrAddAsync(1, "fields", "t:1", Load);
        await cache.GetOrAddAsync(1, "fields", "t:2", Load);
        await cache.GetOrAddAsync(1, "tables", "t:1", Load);

        cache.Invalidate(1, "fields");
        await cache.GetOrAddAsync(1, "fields", "t:1", Load);
        await cache.GetOrAddAsync(1, "fields", "t:2", Load);
        await cache.GetOrAddAsync(1, "tables", "t:1", Load);

        loads.Should().Be(5); // 3 first reads + 2 reloads; the "tables" entry survived
    }

    [Fact]
    public async Task Tenants_do_not_share_entries()
    {
        var cache = Create();
        var loads = 0;
        Task<string> Load() { loads++; return Task.FromResult("v"); }

        await cache.GetOrAddAsync(1, "fields", "t:1", Load);
        await cache.GetOrAddAsync(2, "fields", "t:1", Load);
        cache.Invalidate(1, "fields");
        await cache.GetOrAddAsync(2, "fields", "t:1", Load);

        loads.Should().Be(2);
    }

    [Fact]
    public async Task A_read_that_overlaps_a_write_does_not_store_the_old_data()
    {
        var cache = Create();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var slowRead = cache.GetOrAddAsync(1, "fields", "t:1", async () =>
        {
            started.SetResult();
            await release.Task;
            return "old";
        });
        await started.Task;
        cache.Invalidate(1, "fields"); // the write lands while the read is still running
        release.SetResult();
        (await slowRead).Should().Be("old");

        var fresh = await cache.GetOrAddAsync(1, "fields", "t:1", () => Task.FromResult("new"));
        fresh.Should().Be("new");
    }

    [Fact]
    public async Task Group_stays_uncached_during_the_write_quarantine()
    {
        var cache = Create(quarantineSeconds: 60);
        var loads = 0;
        Task<string> Load() { loads++; return Task.FromResult("v"); }

        cache.Invalidate(1, "fields", inTransaction: true);
        await cache.GetOrAddAsync(1, "fields", "t:1", Load);
        await cache.GetOrAddAsync(1, "fields", "t:1", Load);

        loads.Should().Be(2);
    }

    [Fact]
    public async Task Disabled_cache_always_loads()
    {
        var cache = Create(enabled: false);
        var loads = 0;
        Task<string> Load() { loads++; return Task.FromResult("v"); }

        await cache.GetOrAddAsync(1, "fields", "t:1", Load);
        await cache.GetOrAddAsync(1, "fields", "t:1", Load);

        loads.Should().Be(2);
    }

    [Fact]
    public async Task FlushTenant_clears_all_groups_of_that_tenant()
    {
        var cache = Create();
        var loads = 0;
        Task<string> Load() { loads++; return Task.FromResult("v"); }
        await cache.GetOrAddAsync(1, "fields", "t:1", Load);
        await cache.GetOrAddAsync(1, "tables", "t:1", Load);

        cache.FlushTenant(1);
        await cache.GetOrAddAsync(1, "fields", "t:1", Load);
        await cache.GetOrAddAsync(1, "tables", "t:1", Load);

        loads.Should().Be(4);
    }
}
