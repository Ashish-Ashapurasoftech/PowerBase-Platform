using System.Collections.Concurrent;
using FluentAssertions;
using NSubstitute;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PowerBase.Application.Common.Caching;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Services;
using Xunit;

namespace PowerBase.UnitTests.Common;

/// <summary>
/// The optional Redis L2 of the metadata cache (an in-memory IDistributedCache stands in for Redis): a second instance gets
/// definitions from L2 instead of SQL, a moved shared version means a new key (never a stale read), security groups never go to
/// L2, and a broken L2 only costs a SQL read.
/// </summary>
public class MetadataCacheL2Tests
{
    private const long Tenant = 45;

    private sealed class SharedVersions : ICacheVersionStore
    {
        public readonly ConcurrentDictionary<(long, string), long> Rows = new();
        public Task<IReadOnlyDictionary<string, long>?> ReadAsync(long tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, long>?>(Rows.Where(r => r.Key.Item1 == tenantId).ToDictionary(r => r.Key.Item2, r => r.Value));
        public bool Bump(long tenantId, string group) { Rows.AddOrUpdate((tenantId, group), 1, (_, v) => v + 1); return true; }
    }

    private sealed class BrokenRedis : IDistributedCache
    {
        public byte[]? Get(string key) => throw new TimeoutException();
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => throw new TimeoutException();
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new TimeoutException();
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => throw new TimeoutException();
        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key) { }
        public Task RemoveAsync(string key, CancellationToken token = default) => Task.CompletedTask;
    }

    private static MetadataCache Instance(ICacheVersionStore versions, IDistributedCache l2) =>
        new(new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new MetadataCacheOptions { TransactionQuarantineSeconds = 0, WriteQuarantineSeconds = 0 }),
            NullLogger<MetadataCache>.Instance, versions, l2);

    private static IReadOnlyList<AppField> Fields(string name) =>
    [
        new AppField { Id = 101, Fid = 6, Name = name, TypeCode = "Text", PhysicalColumnName = "f_6", IsSearchable = true, Settings = """{"maxLength":80}""" },
        new AppField { Id = 102, Fid = 7, Name = "Amount", TypeCode = "Currency", IsEncrypted = false },
    ];

    private static async Task<IReadOnlyList<AppField>> Request(MetadataCache instance, Func<IReadOnlyList<AppField>> sql)
    {
        using var _ = CacheVersionScope.Begin();
        return await instance.GetOrAddAsync(Tenant, MetadataCacheGroups.Fields, "table:5", () => Task.FromResult(sql()));
    }

    private static async Task WaitForL2Write(MemoryDistributedCache l2, string keyPart)
    {
        // The L2 write is not awaited by the request (fire-and-forget); give it a moment.
        for (var i = 0; i < 50; i++)
        {
            if (await l2.GetAsync($"pb:t{Tenant}:fields:{keyPart}:table:5") is not null) return;
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task A_second_instance_reads_the_definition_from_L2_instead_of_SQL()
    {
        var versions = new SharedVersions();
        var l2 = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var sqlReads = 0;

        var fromA = await Request(Instance(versions, l2), () => { sqlReads++; return Fields("Name"); });
        await WaitForL2Write(l2, "v0");
        var fromB = await Request(Instance(versions, l2), () => { sqlReads++; return Fields("Name"); });

        sqlReads.Should().Be(1, "instance B found it in L2");
        fromB.Should().BeEquivalentTo(fromA, "the JSON round trip keeps every property");
    }

    [Fact]
    public async Task After_a_write_the_new_version_is_a_new_key_so_nothing_stale_is_read()
    {
        var versions = new SharedVersions();
        var l2 = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var a = Instance(versions, l2);
        await Request(a, () => Fields("Old name"));
        await WaitForL2Write(l2, "v0");

        a.Invalidate(Tenant, MetadataCacheGroups.Fields);   // field renamed: shared version 0 → 1

        var fromB = await Request(Instance(versions, l2), () => Fields("New name"));
        fromB[0].Name.Should().Be("New name");
    }

    /// <summary>Multi-instance without L1 (UseInMemoryCache = false): permissions come from Redis on the next instance, not SQL every time.</summary>
    [Fact]
    public async Task Permissions_are_shared_through_L2_under_their_version()
    {
        var versions = new SharedVersions();
        var l2 = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        MetadataCache NoL1() => new(new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new MetadataCacheOptions { TransactionQuarantineSeconds = 0, WriteQuarantineSeconds = 0, UseInMemoryCache = false }),
            NullLogger<MetadataCache>.Instance, versions, l2);
        var sqlReads = 0;
        async Task<IReadOnlySet<string>> Perms(MetadataCache c, string code)
        {
            using var _ = CacheVersionScope.Begin();
            return await c.GetOrAddAsync<IReadOnlySet<string>>(Tenant, MetadataCacheGroups.Access, "appperms:1:2",
                () => { sqlReads++; return Task.FromResult<IReadOnlySet<string>>(new HashSet<string> { code }); });
        }

        await Perms(NoL1(), "records.delete");
        for (var i = 0; i < 50 && await l2.GetAsync($"pb:t{Tenant}:access:v0:appperms:1:2") is null; i++) await Task.Delay(20);
        (await Perms(NoL1(), "records.delete")).Should().BeEquivalentTo(["records.delete"], "IReadOnlySet is read back as a HashSet");
        sqlReads.Should().Be(1);

        // Revoked: the role change bumps the access version, so the next request reads a new key — never the old permission.
        NoL1().Invalidate(Tenant, MetadataCacheGroups.Access);
        (await Perms(NoL1(), "records.read")).Should().BeEquivalentTo(["records.read"]);
    }

    [Fact]
    public async Task Without_readable_versions_permissions_never_come_from_L2()
    {
        var versions = Substitute.For<ICacheVersionStore>();
        versions.ReadAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyDictionary<string, long>?>(null));
        versions.ReadWithAgeAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyDictionary<string, CacheGroupVersion>?>(null));
        var l2 = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        await l2.SetAsync($"pb:t{Tenant}:access:v0:appperms:1:2", System.Text.Encoding.UTF8.GetBytes("""["records.delete"]"""));
        using var _ = CacheVersionScope.Begin();
        var result = await Instance(versions, l2).GetOrAddAsync<IReadOnlySet<string>>(Tenant, MetadataCacheGroups.Access, "appperms:1:2",
            () => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>()));
        result.Should().BeEmpty("fail closed: SQL answers when the versions cannot be read, whatever Redis holds");
    }

    /// <summary>Every type the security groups cache comes back from JSON with every property equal.</summary>
    [Fact]
    public void Security_types_round_trip_through_JSON_exactly()
    {
        AssertRoundTrip(Filled<AppUser>());
        AssertRoundTrip(Filled<AppRoleTablePermission>());
        AssertRoundTrip(Filled<AppRoleRecordFilter>());
        AssertRoundTrip(Filled<Report>());
        MetadataCache.FromL2<IReadOnlySet<string>>(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new HashSet<string> { "a", "b" }))
            .Should().BeEquivalentTo(["a", "b"]);
        MetadataCache.FromL2<IReadOnlyDictionary<long, string>>(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new Dictionary<long, string> { [6] = "Edit" }))
            .Should().BeEquivalentTo(new Dictionary<long, string> { [6] = "Edit" });
        MetadataCache.FromL2<IReadOnlyList<long>>(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new List<long> { 3, 4 }))
            .Should().BeEquivalentTo([3L, 4L]);
        MetadataCache.FromL2<string>(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes("1")).Should().Be("1");
    }

    /// <summary>Versions with their age on the "database clock", as SqlCacheVersionStore reads them; Bump may be made to fail.</summary>
    private sealed class AgedVersions : ICacheVersionStore
    {
        public readonly ConcurrentDictionary<string, (long V, long AgeMs)> Rows = new(StringComparer.Ordinal);
        public bool BumpFails;
        public Task<IReadOnlyDictionary<string, long>?> ReadAsync(long tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, long>?>(Rows.ToDictionary(r => r.Key, r => r.Value.V));
        public Task<IReadOnlyDictionary<string, CacheGroupVersion>?> ReadWithAgeAsync(long tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, CacheGroupVersion>?>(Rows.ToDictionary(r => r.Key, r => new CacheGroupVersion(r.Value.V, r.Value.AgeMs)));
        public bool Bump(long tenantId, string group)
        {
            if (BumpFails) return false;
            Rows.AddOrUpdate(group, (1, 0), (_, r) => (r.V + 1, 0));
            return true;
        }
    }

    private static MetadataCache Quarantined(ICacheVersionStore versions, IDistributedCache l2) =>
        new(new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new MetadataCacheOptions { TransactionQuarantineSeconds = 30, WriteQuarantineSeconds = 5 }),
            NullLogger<MetadataCache>.Instance, versions, l2);

    [Fact]
    public async Task A_version_that_just_moved_is_not_shared_so_an_open_write_can_never_leave_the_old_value_in_L2()
    {
        // Instance A bumped "fields" to v3 a moment ago and its transaction (the rename) is still open. Instance C reads SQL now and
        // gets the OLD name. Shared under v3 it would be served to every instance for hours; it must stay out of L2.
        var versions = new AgedVersions();
        versions.Rows["fields"] = (3, 200);
        var l2 = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

        var fromC = await Request(Quarantined(versions, l2), () => Fields("Old name"));
        await Task.Delay(200);

        fromC[0].Name.Should().Be("Old name");
        (await l2.GetAsync($"pb:t{Tenant}:fields:v3:table:5")).Should().BeNull("v3 is younger than the 30 s transaction quarantine");
    }

    [Fact]
    public async Task A_settled_version_is_shared()
    {
        var versions = new AgedVersions();
        versions.Rows["fields"] = (3, 31_000);
        var l2 = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var sqlReads = 0;

        await Request(Quarantined(versions, l2), () => { sqlReads++; return Fields("Name"); });
        await WaitForL2Write(l2, "v3");
        await Request(Quarantined(versions, l2), () => { sqlReads++; return Fields("Name"); });

        sqlReads.Should().Be(1, "the second instance read v3 from L2");
    }

    [Fact]
    public async Task A_failed_bump_stops_this_instance_from_reading_that_group_from_L2()
    {
        // The rename's version bump failed: L2 still holds the old value under the unmoved v0. The writing instance must see its own change.
        var versions = new AgedVersions { BumpFails = true };
        var l2 = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        await Request(Instance(versions, l2), () => Fields("Old name"));
        await WaitForL2Write(l2, "v0");
        (await l2.GetAsync($"pb:t{Tenant}:fields:v0:table:5")).Should().NotBeNull();

        var a = Instance(versions, l2);
        a.Invalidate(Tenant, MetadataCacheGroups.Fields);

        (await Request(a, () => Fields("New name")))[0].Name.Should().Be("New name");
    }

    [Fact]
    public async Task Another_tenant_never_reads_a_tenants_L2_entry()
    {
        var versions = new SharedVersions();
        var l2 = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        await Request(Instance(versions, l2), () => Fields("Tenant 45 field"));
        await WaitForL2Write(l2, "v0");

        using var _ = CacheVersionScope.Begin();
        var other = await Instance(versions, l2).GetOrAddAsync(46, MetadataCacheGroups.Fields, "table:5",
            () => Task.FromResult<IReadOnlyList<AppField>>([new AppField { Id = 1, Fid = 6, Name = "Tenant 46 field", TypeCode = "Text" }]));

        other.Single().Name.Should().Be("Tenant 46 field");
    }

    [Theory]
    [InlineData(45L, "fields", 3L, "table:5")]
    [InlineData(1L, "ids", 0L, "app:00000000-0000-0000-0000-000000000001")]
    public void Every_key_starts_with_the_tenant_after_the_prefix(long tenant, string group, long version, string key) =>
        MetadataCache.L2KeyFor("pb", tenant, group, version, key).Should().StartWith($"pb:t{tenant}:{group}:v{version}:");

    /// <summary>Every type the L2 groups hold comes back from JSON with every property equal (a property JSON could not set would come
    /// back empty and be served as if it were the definition).</summary>
    [Fact]
    public void Every_cached_type_round_trips_through_JSON_exactly()
    {
        AssertRoundTrip(Filled<App>());
        AssertRoundTrip(Filled<AppTable>());
        AssertRoundTrip(Filled<AppField>());
        AssertRoundTrip(new PowerBase.Infrastructure.Repositories.CachedId(42));
        var list = (IReadOnlyList<AppField>)new List<AppField> { Filled<AppField>(), Filled<AppField>() };
        System.Text.Json.JsonSerializer.Deserialize<IReadOnlyList<AppField>>(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(list))
            .Should().BeEquivalentTo(list);
    }

    private static void AssertRoundTrip<T>(T value)
    {
        var back = System.Text.Json.JsonSerializer.Deserialize<T>(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value));
        back.Should().BeEquivalentTo(value);
    }

    /// <summary>Every writable public property set to a non-default value, so a property lost on the way back shows up.</summary>
    private static T Filled<T>() where T : new()
    {
        var o = new T();
        var n = 1;
        foreach (var p in typeof(T).GetProperties().Where(p => p.CanWrite && p.GetSetMethod() is not null))
        {
            n++;
            var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            object? v = t == typeof(string) ? $"s{n}"
                : t == typeof(bool) ? true
                : t == typeof(long) ? (long)n
                : t == typeof(int) ? n
                : t == typeof(byte) ? (byte)n
                : t == typeof(decimal) ? n + 0.25m
                : t == typeof(Guid) ? Guid.NewGuid()
                : t == typeof(DateTime) ? new DateTime(2026, 11, 27, 10, 0, n % 60, DateTimeKind.Unspecified)
                : t == typeof(byte[]) ? new byte[] { 1, (byte)n }
                : null;
            if (v is not null) p.SetValue(o, v);
        }
        return o;
    }

    [Fact]
    public async Task A_broken_L2_only_costs_a_SQL_read()
    {
        var versions = new SharedVersions();
        var sqlReads = 0;
        var result = await Request(Instance(versions, new BrokenRedis()), () => { sqlReads++; return Fields("Name"); });
        result.Should().HaveCount(2);
        sqlReads.Should().Be(1);
    }

    /// <summary>The real StackExchange.Redis client with no Redis listening (Redis down): requests still answer from SQL, and after the
    /// first failure L2 is skipped for 30 s, so only that first request pays the wait.</summary>
    [Fact]
    public async Task Redis_down_answers_from_SQL_and_only_the_first_request_waits()
    {
        var redis = new Microsoft.Extensions.Caching.StackExchangeRedis.RedisCache(Options.Create(
            new Microsoft.Extensions.Caching.StackExchangeRedis.RedisCacheOptions
            {
                Configuration = "127.0.0.1:6399,abortConnect=false,connectTimeout=300,syncTimeout=300,asyncTimeout=300,connectRetry=0",
            }));
        var instance = Instance(new SharedVersions(), redis);

        var first = System.Diagnostics.Stopwatch.StartNew();
        (await Request(instance, () => Fields("Name"))).Should().HaveCount(2);
        first.Stop();

        var later = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            using var _ = CacheVersionScope.Begin();
            await instance.GetOrAddAsync(Tenant, MetadataCacheGroups.Fields, $"table:{100 + i}", () => Task.FromResult(Fields("Name")));
        }
        later.Stop();

        first.ElapsedMilliseconds.Should().BeLessThan(10_000, "one connect attempt; slower when the whole suite runs in parallel");
        later.ElapsedMilliseconds.Should().BeLessThan(1_000, "L2 is skipped while it is down");
    }

    // ── Version stamp of the result cache ───────────────────────────────────

    [Fact]
    public async Task The_stamp_moves_with_any_tenant_or_global_version_and_is_withheld_while_one_just_moved()
    {
        var versions = new AgedVersions();
        versions.Rows["fields"] = (3, 60_000);
        versions.Rows["access"] = (5, 60_000);
        var l2 = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

        async Task<string?> Stamp()
        {
            using var _ = CacheVersionScope.Begin();
            return await Quarantined(versions, l2).VersionStampAsync(Tenant);
        }

        var before = await Stamp();
        before.Should().NotBeNull();

        versions.Rows["access"] = (6, 60_000);   // a role permission changed (long enough ago to be settled)
        var afterRoleChange = await Stamp();
        afterRoleChange.Should().NotBe(before);

        versions.Rows["userperms"] = (1, 60_000);   // a global group (AgedVersions answers every tenant, the global one included)
        (await Stamp()).Should().NotBe(afterRoleChange);

        versions.Rows["fields"] = (4, 500);   // moved half a second ago: its write may still be open
        (await Stamp()).Should().BeNull();
    }

    [Fact]
    public async Task No_stamp_without_shared_versions()
    {
        using var _ = CacheVersionScope.Begin();
        var cache = new MetadataCache(new MemoryCache(new MemoryCacheOptions()), Options.Create(new MetadataCacheOptions()), NullLogger<MetadataCache>.Instance);
        (await cache.VersionStampAsync(Tenant)).Should().BeNull();
    }
}
