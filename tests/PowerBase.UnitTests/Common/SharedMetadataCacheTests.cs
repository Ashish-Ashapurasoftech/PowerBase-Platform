using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PowerBase.Application.Common.Caching;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Infrastructure.Services;
using Xunit;

namespace PowerBase.UnitTests.Common;

/// <summary>
/// ARC-01: two API instances (two caches over one shared version store) — a write on one is honoured by the other on its next request,
/// security groups fail closed when the versions can't be read, and concurrent misses share one load.
/// </summary>
public class SharedMetadataCacheTests
{
    private const long Tenant = 45;

    /// <summary>meta.CacheVersion, shared by every instance.</summary>
    private sealed class SharedVersions : ICacheVersionStore
    {
        public readonly ConcurrentDictionary<(long, string), long> Rows = new();
        public bool Down;
        public int Reads;

        public Task<IReadOnlyDictionary<string, long>?> ReadAsync(long tenantId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Reads);
            if (Down) return Task.FromResult<IReadOnlyDictionary<string, long>?>(null);
            IReadOnlyDictionary<string, long> rows = Rows.Where(r => r.Key.Item1 == tenantId).ToDictionary(r => r.Key.Item2, r => r.Value);
            return Task.FromResult<IReadOnlyDictionary<string, long>?>(rows);
        }

        public bool Bump(long tenantId, string group)
        {
            if (Down) return false;
            Rows.AddOrUpdate((tenantId, group), 1, (_, v) => v + 1);
            return true;
        }
    }

    private static MetadataCache Instance(SharedVersions versions, int quarantineSeconds = 30) =>
        new(new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new MetadataCacheOptions { TransactionQuarantineSeconds = quarantineSeconds, WriteQuarantineSeconds = quarantineSeconds }),
            NullLogger<MetadataCache>.Instance, versions);

    /// <summary>One request on an instance: reads through the cache inside a request scope.</summary>
    private static async Task<string> Request(MetadataCache instance, string group, Func<string> database)
    {
        using var _ = CacheVersionScope.Begin();
        return await instance.GetOrAddAsync(Tenant, group, "k", () => Task.FromResult(database()));
    }

    [Fact]
    public async Task A_permission_revoked_on_one_instance_is_honoured_by_the_other_on_its_next_request()
    {
        var versions = new SharedVersions();
        var a = Instance(versions, quarantineSeconds: 0);
        var b = Instance(versions, quarantineSeconds: 0);
        var permission = "can-delete";

        (await Request(b, MetadataCacheGroups.Access, () => permission)).Should().Be("can-delete");   // B caches it

        a.Invalidate(Tenant, MetadataCacheGroups.Access);   // A revokes (bump before the write) …
        permission = "read-only";                           // … and writes

        (await Request(b, MetadataCacheGroups.Access, () => permission)).Should().Be("read-only", "B's next request sees the moved version");
    }

    [Fact]
    public async Task Within_one_request_the_versions_are_read_once()
    {
        var versions = new SharedVersions();
        var b = Instance(versions);
        using (CacheVersionScope.Begin())
        {
            for (var i = 0; i < 5; i++)
                await b.GetOrAddAsync(Tenant, MetadataCacheGroups.Fields, $"k{i}", () => Task.FromResult("v"));
        }
        versions.Reads.Should().Be(1);
    }

    [Fact]
    public async Task While_another_instance_writes_the_group_is_not_cached_for_the_quarantine()
    {
        var versions = new SharedVersions();
        var a = Instance(versions);
        var b = Instance(versions);
        var value = "old";
        await Request(b, MetadataCacheGroups.Fields, () => value);

        a.Invalidate(Tenant, MetadataCacheGroups.Fields, inTransaction: true);   // A's transaction is still open
        (await Request(b, MetadataCacheGroups.Fields, () => value)).Should().Be("old");   // B reads SQL (the commit hasn't happened)
        value = "new";                                                                 // A commits
        (await Request(b, MetadataCacheGroups.Fields, () => value)).Should().Be("new", "B did not cache the pre-commit read");
    }

    [Fact]
    public async Task Security_groups_fail_closed_when_the_versions_cannot_be_read()
    {
        var versions = new SharedVersions { Down = true };
        var b = Instance(versions);
        var loads = 0;
        for (var i = 0; i < 3; i++)
            await Request(b, MetadataCacheGroups.Access, () => { loads++; return "perm"; });
        loads.Should().Be(3, "never served from the cache without a confirmed version");

        var fieldLoads = 0;
        for (var i = 0; i < 3; i++)
            await Request(b, MetadataCacheGroups.Fields, () => { fieldLoads++; return "fields"; });
        fieldLoads.Should().Be(1, "a non-security group keeps the local cache meanwhile");
    }

    [Fact]
    public async Task When_the_versions_come_back_everything_cached_meanwhile_is_dropped_once()
    {
        var versions = new SharedVersions { Down = true };
        var b = Instance(versions, quarantineSeconds: 0);
        var value = "v1";
        await Request(b, MetadataCacheGroups.Fields, () => value);   // cached while versions were down
        value = "v2";                                                 // changed on another instance meanwhile (its bump failed too)
        versions.Down = false;

        (await Request(b, MetadataCacheGroups.Fields, () => value)).Should().Be("v2");
    }

    [Fact]
    public async Task Concurrent_misses_share_one_load()
    {
        var versions = new SharedVersions();
        var b = Instance(versions);
        var loads = 0;
        var gate = new TaskCompletionSource();
        async Task<string> Load() { Interlocked.Increment(ref loads); await gate.Task; return "fields"; }

        var calls = Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
        {
            using var __ = CacheVersionScope.Begin();
            return await b.GetOrAddAsync(Tenant, MetadataCacheGroups.Fields, "k", Load);
        })).ToList();
        await Task.Delay(100);
        gate.SetResult();
        (await Task.WhenAll(calls)).Should().OnlyContain(v => v == "fields");
        loads.Should().Be(1);
    }

    [Fact]
    public void Flushing_a_tenant_bumps_every_group()
    {
        var versions = new SharedVersions();
        Instance(versions).FlushTenant(Tenant);
        versions.Rows.Keys.Where(k => k.Item1 == Tenant).Select(k => k.Item2).Should().BeEquivalentTo(
            [MetadataCacheGroups.Fields, MetadataCacheGroups.Tables, MetadataCacheGroups.Apps, MetadataCacheGroups.Relationships, MetadataCacheGroups.Access, MetadataCacheGroups.Ids]);
    }

    [Fact]
    public void Security_groups_are_the_permission_and_session_ones()
    {
        MetadataCacheGroups.IsSecurityRelevant(MetadataCacheGroups.Access).Should().BeTrue();
        MetadataCacheGroups.IsSecurityRelevant(MetadataCacheGroups.UserPermissions).Should().BeTrue();
        MetadataCacheGroups.IsSecurityRelevant(MetadataCacheGroups.Sessions).Should().BeTrue();
        MetadataCacheGroups.IsSecurityRelevant(MetadataCacheGroups.Fields).Should().BeFalse();
    }
}
