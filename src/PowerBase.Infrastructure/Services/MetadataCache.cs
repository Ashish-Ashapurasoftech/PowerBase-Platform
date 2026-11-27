using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using PowerBase.Application.Common.Caching;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using StackExchange.Redis;

namespace PowerBase.Infrastructure.Services;

/// <summary>
/// Metadata cache supporting configurable backing stores via <see cref="MetadataCacheOptions.Provider"/>:
/// <c>InMemory</c> (process-local <see cref="IMemoryCache"/>), <c>Redis</c> (L1 memory + Redis L2), or
/// <c>AzureCache</c> (L1 memory + Azure Cache for Redis over TLS). Shared versions in SQL (<see cref="ICacheVersionStore"/>)
/// reach every API instance (ARC-01). Security groups (<see cref="MetadataCacheGroups.IsSecurityRelevant"/>) are never served
/// from cache when shared versions cannot be read.
/// </summary>
public sealed class MetadataCache : IMetadataCache, IDisposable
{
    private sealed class GroupState
    {
        public CancellationTokenSource Cts = new();
        public long Version;
        public long SharedVersion;
        public long QuarantineUntilTicks;
    }

    /// <summary>What this process last saw of a tenant's shared versions.</summary>
    private sealed class TenantVersions
    {
        public readonly Dictionary<string, long> Seen = new(StringComparer.Ordinal);
        /// <summary>When the version in <see cref="Seen"/> is older than the transaction quarantine (local clock), so the write it
        /// announced has committed: only from then on may a value read from SQL be shared through L2 under that version.</summary>
        public readonly Dictionary<string, long> SettledAtTicks = new(StringComparer.Ordinal);
        public long CheckedAtTicks;
        public bool Available;
        public bool EverRead;
    }

    private static readonly string[] TenantGroups =
        [MetadataCacheGroups.Fields, MetadataCacheGroups.Tables, MetadataCacheGroups.Apps, MetadataCacheGroups.Relationships, MetadataCacheGroups.Access, MetadataCacheGroups.Ids];
    private static readonly string[] GlobalGroups = [MetadataCacheGroups.UserPermissions, MetadataCacheGroups.Sessions];
    private static readonly JsonSerializerOptions RedisJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IMemoryCache _cache;
    private readonly MetadataCacheOptions _options;
    private readonly ILogger<MetadataCache> _logger;
    private readonly ICacheVersionStore? _versions;
    private readonly CacheProviderKind _provider;
    private readonly string? _distributedConn;
    private readonly Lazy<ConnectionMultiplexer?> _redis;
    private readonly ConcurrentDictionary<(long Tenant, string Group), GroupState> _groups = new();
    private readonly ConcurrentDictionary<long, TenantVersions> _tenantVersions = new();

    /// <summary>Loads in flight by cache key: concurrent misses of one key share one factory call (no stampede at morning start-up).</summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> _inflight = new(StringComparer.Ordinal);

    /// <summary>Optional L2 (Azure Managed Redis): shared between instances so a new or restarted instance does not reload every
    /// definition from SQL. Only non-security groups, keyed by the group's shared SQL version (a moved version = a different key, so a
    /// stale entry is never read). Never trusted for permissions: those stay L1 + SQL.</summary>
    private readonly IDistributedCache? _l2;
    private long _l2DownUntilTicks;
    /// <summary>Groups whose version bump failed here: their L2 entries (keyed by the unmoved version) may hold the old value, so this
    /// instance does not use L2 for them for a whole TTL. Other instances behave as without L2 (they too see the change only at TTL).</summary>
    private readonly ConcurrentDictionary<(long Tenant, string Group), long> _l2BlockedUntilTicks = new();
    private static readonly System.Text.Json.JsonSerializerOptions L2Json = new(System.Text.Json.JsonSerializerDefaults.General);
    private static readonly HashSet<string> L2Groups = new(StringComparer.Ordinal)
        { MetadataCacheGroups.Fields, MetadataCacheGroups.Tables, MetadataCacheGroups.Apps, MetadataCacheGroups.Relationships, MetadataCacheGroups.Ids };

    public MetadataCache(IMemoryCache cache, IOptions<MetadataCacheOptions> options, ILogger<MetadataCache> logger, ICacheVersionStore? versions = null,
        IDistributedCache? l2 = null)
    {
        _options = options.Value;
        _l2 = _options.SharedVersions && versions is not null ? l2 : null;   // L2 keys need the shared versions
        // Bounded memory: its own cache with a size limit (entries of 1 unit each). Not the shared IMemoryCache, whose other users set
        // no entry sizes (a size-limited cache refuses those).
        _cache = _options.SizeLimitEntries > 0
            ? new MemoryCache(new MemoryCacheOptions { SizeLimit = _options.SizeLimitEntries, CompactionPercentage = 0.2 })
            : cache;
        _logger = logger;
        _versions = _options.SharedVersions ? versions : null;
        _provider = _options.ResolveProvider();
        _distributedConn = _options.ResolveDistributedConnectionString();
        _redis = new Lazy<ConnectionMultiplexer?>(ConnectDistributed, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public CacheProviderKind ActiveProvider =>
        _provider != CacheProviderKind.InMemory && !string.IsNullOrWhiteSpace(_distributedConn) ? _provider : CacheProviderKind.InMemory;

    public async Task<T> GetOrAddAsync<T>(long tenantId, string group, string key, Func<Task<T>> factory) where T : class
    {
        if (CacheMetrics.SummaryDue())
            _logger.LogInformation("Cache metrics since start: {Metrics}", string.Join(", ", CacheMetrics.Snapshot().Select(m => $"{m.Key}={m.Value}")));
        if (!_options.Enabled)
        {
            CacheMetrics.Lookup(group, CacheMetrics.Source.Bypass);
            return await factory();
        }

        // Other instances' writes first: a moved version drops the group here before it is read. Without readable versions a
        // security group is answered from SQL every time (fail closed); other groups keep the local behaviour.
        if (_versions is not null && !await SyncAsync(tenantId) && MetadataCacheGroups.IsSecurityRelevant(group))
        {
            CacheMetrics.Lookup(group, CacheMetrics.Source.Bypass);
            return await factory();
        }

        var state = _groups.GetOrAdd((tenantId, group), _ => new GroupState());
        var cacheKey = $"mc:{tenantId}:{group}:{key}";
        if (_options.UseInMemoryCache && _cache.TryGetValue(cacheKey, out T? hit) && hit is not null)
        {
            _logger.LogDebug("Metadata cache L1 hit {Key}", cacheKey);
            CacheMetrics.Lookup(group, CacheMetrics.Source.L1);
            return hit;
        }

        long version;
        long sharedVersion;
        CancellationToken token;
        bool quarantined;
        lock (state)
        {
            version = state.Version;
            sharedVersion = state.SharedVersion;
            token = state.Cts.Token;
            quarantined = DateTime.UtcNow.Ticks < state.QuarantineUntilTicks;
        }

        T? value;
        if (quarantined)
        {
            CacheMetrics.Lookup(group, CacheMetrics.Source.Bypass);
            value = await factory();   // not stored anyway: no point sharing the load
        }
        else
        {
            var mine = new Lazy<Task<object?>>(async () => await LoadAsync(tenantId, group, sharedVersion, key, factory));
            var load = _inflight.GetOrAdd(cacheKey, mine);
            // The loading request counts where it was answered (L2 or SQL, inside LoadAsync); the others waited for it.
            if (!ReferenceEquals(load, mine)) CacheMetrics.Lookup(group, CacheMetrics.Source.Shared);
            try { value = (T?)await load.Value; }
            finally { _inflight.TryRemove(new KeyValuePair<string, Lazy<Task<object?>>>(cacheKey, load)); }
        }
        _logger.LogDebug("Metadata cache miss {Key} (provider={Provider})", cacheKey, ActiveProvider);
        if (value is null || quarantined || !_options.UseInMemoryCache) return value!;

        lock (state)
        {
            // A write started while the factory ran: what it read may already be out of date.
            if (state.Version != version) return value;
        }

        // The entry hangs on the group's token, so a write that lands right after the check above still evicts it.
        var entry = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(Math.Max(1, _options.TtlHours)) }
            .AddExpirationToken(new CancellationChangeToken(token));
        // A size-limited IMemoryCache (CacheMemory:SizeLimitEntries) needs a size on every entry; one entry = one unit.
        entry.Size = 1;
        _cache.Set(cacheKey, value, entry);
        return value;
    }

    /// <summary>L1 miss: the shared L2 first (when on and healthy), then SQL — whose answer is also put in L2 for the other instances.</summary>
    private async Task<object?> LoadAsync<T>(long tenantId, string group, long sharedVersion, string key, Func<Task<T>> factory) where T : class
    {
        var (l2Key, mayShare) = L2Key(tenantId, group, key);
        if (l2Key is not null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(10, _options.L2TimeoutMs)));
                var bytes = await _l2!.GetAsync(l2Key, timeout.Token);
                if (bytes is { Length: > 0 } && System.Text.Json.JsonSerializer.Deserialize<T>(bytes, L2Json) is { } fromL2)
                {
                    CacheMetrics.Lookup(group, CacheMetrics.Source.L2);
                    return fromL2;
                }
            }
            catch (Exception ex)
            {
                L2Down(ex);
                l2Key = null;
            }
        }
        else if (_l2 is null && ActiveProvider != CacheProviderKind.InMemory && !MetadataCacheGroups.IsSecurityRelevant(group)
            && await TryGetDistributedAsync<T>(tenantId, group, sharedVersion, key) is { } muxHit)
        {
            CacheMetrics.Lookup(group, CacheMetrics.Source.L2);
            return muxHit;
        }

        CacheMetrics.Lookup(group, CacheMetrics.Source.Sql);
        var value = await factory();
        // Decided before the SQL read started: a version that moved less than the quarantine ago may announce a write still open, and
        // what SQL returned may be the old value — shared under the new version it would be served to every instance for hours.
        if (l2Key is not null && mayShare && value is not null)
        {
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value, L2Json);
            // Not awaited by the request: L2 is a shared speed-up, never on the critical path.
            _ = Task.Run(async () =>
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _l2!.SetAsync(l2Key, bytes, new DistributedCacheEntryOptions
                        { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(Math.Max(1, _options.TtlHours)) }, timeout.Token);
                    CacheMetrics.L2BytesWritten(group, bytes.Length);
                }
                catch (Exception ex) { L2Down(ex); }
            });
        }
        else if (_l2 is null && value is not null && ActiveProvider != CacheProviderKind.InMemory && !MetadataCacheGroups.IsSecurityRelevant(group))
        {
            await TrySetDistributedAsync(tenantId, group, sharedVersion, key, value);
        }
        return value;
    }

    /// <summary>
    /// The L2 key, or null when L2 must not be used: off, down, a security group, the group's shared version unknown, or its bump failed
    /// here. <c>MayShare</c>: a value read from SQL now may be written under that key (the version has settled, see
    /// <see cref="TenantVersions.SettledAtTicks"/>). Reading is always allowed: no value is ever written for an unsettled version.
    /// </summary>
    private (string? Key, bool MayShare) L2Key(long tenantId, string group, string key)
    {
        var now = DateTime.UtcNow.Ticks;
        if (_l2 is null || !L2Groups.Contains(group) || now < Interlocked.Read(ref _l2DownUntilTicks)) return (null, false);
        if (_l2BlockedUntilTicks.TryGetValue((tenantId, group), out var blockedUntil) && now < blockedUntil) return (null, false);
        if (!_tenantVersions.TryGetValue(tenantId, out var tv)) return (null, false);
        long version;
        bool settled;
        lock (tv)
        {
            if (!tv.Available) return (null, false);
            version = tv.Seen.TryGetValue(group, out var v) ? v : 0;
            settled = !tv.SettledAtTicks.TryGetValue(group, out var at) || now >= at;
        }
        return (L2KeyFor(_options.L2KeyPrefix, tenantId, group, version, key), settled);
    }

    /// <summary>The only place an L2 key is built: tenant first, then group and its shared version (doc 23 §4).</summary>
    internal static string L2KeyFor(string prefix, long tenantId, string group, long version, string key) =>
        $"{prefix}:t{tenantId}:{group}:v{version}:{key}";

    private void L2Down(Exception ex)
    {
        Interlocked.Exchange(ref _l2DownUntilTicks, (DateTime.UtcNow + TimeSpan.FromSeconds(30)).Ticks);
        CacheMetrics.L2Error();
        _logger.LogWarning(ex, "Metadata L2 cache (Redis) unavailable; using SQL for 30 s");
    }

    public void Invalidate(long tenantId, string group, bool inTransaction = false)
    {
        if (!_options.Enabled) return;
        InvalidateLocal(tenantId, group, inTransaction);
        // Every other instance: the shared version moves now (before the write), so they stop caching the group on their next request.
        if (_versions is not null && !_versions.Bump(tenantId, group) && _l2 is not null)
            _l2BlockedUntilTicks[(tenantId, group)] = (DateTime.UtcNow + TimeSpan.FromHours(Math.Max(1, _options.TtlHours))).Ticks;
    }

    public async Task<string?> VersionStampAsync(long tenantId)
    {
        if (!_options.Enabled || _versions is null) return null;
        if (!await SyncAsync(tenantId) || !await SyncAsync(MetadataCacheGroups.GlobalTenant)) return null;
        var now = DateTime.UtcNow.Ticks;
        string? Part(long t)
        {
            if (!_tenantVersions.TryGetValue(t, out var tv)) return null;
            lock (tv)
            {
                if (!tv.Available) return null;
                // A version younger than the quarantine (age unknown counts as "just moved") may announce a write still open.
                if (tv.SettledAtTicks.Values.Any(at => now < at)) return null;
                return string.Join(",", tv.Seen.OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}={g.Value}"));
            }
        }
        var tenant = Part(tenantId);
        var global = Part(MetadataCacheGroups.GlobalTenant);
        return tenant is null || global is null ? null : $"{tenant}|{global}";
    }

    public void FlushTenant(long tenantId)
    {
        var groups = tenantId == MetadataCacheGroups.GlobalTenant ? GlobalGroups : TenantGroups;
        foreach (var group in groups.Union(_groups.Keys.Where(k => k.Tenant == tenantId).Select(k => k.Group)).ToList())
            Invalidate(tenantId, group);
    }

    private void InvalidateLocal(long tenantId, string group, bool inTransaction)
    {
        var state = _groups.GetOrAdd((tenantId, group), _ => new GroupState());
        var quarantine = TimeSpan.FromSeconds(inTransaction ? _options.TransactionQuarantineSeconds : _options.WriteQuarantineSeconds);
        CancellationTokenSource old;
        lock (state)
        {
            state.Version++;
            state.SharedVersion++;
            state.QuarantineUntilTicks = Math.Max(state.QuarantineUntilTicks, (DateTime.UtcNow + quarantine).Ticks);
            old = state.Cts;
            state.Cts = new CancellationTokenSource();
        }
        old.Cancel();
    }

    /// <summary>Reads the tenant's shared versions when due and drops every group whose version moved since this process last looked.
    /// Returns whether the versions are known (false = unreadable: security groups must bypass the cache).</summary>
    private async Task<bool> SyncAsync(long tenantId)
    {
        var tv = _tenantVersions.GetOrAdd(tenantId, _ => new TenantVersions());
        var scope = CacheVersionScope.Current;
        bool due;
        if (scope is not null)
            due = scope.FirstUse(tenantId);
        else
            lock (tv)
                due = DateTime.UtcNow.Ticks - tv.CheckedAtTicks >= TimeSpan.FromMilliseconds(Math.Max(0, _options.VersionCheckIntervalMs)).Ticks;
        if (!due)
        {
            lock (tv) return tv.Available;
        }

        // With L2 on, the ages too (one query either way): they say when a version has settled. Without them, "just moved".
        IReadOnlyDictionary<string, CacheGroupVersion>? withAge = _l2 is null ? null : await _versions!.ReadWithAgeAsync(tenantId);
        var remote = withAge?.ToDictionary(r => r.Key, r => r.Value.Version, StringComparer.Ordinal) ?? await _versions!.ReadAsync(tenantId);
        var moved = new List<string>();
        lock (tv)
        {
            var nowTicks = DateTime.UtcNow.Ticks;
            tv.CheckedAtTicks = nowTicks;
            if (remote is null)
            {
                tv.Available = false;
                return false;
            }
            var quarantineTicks = TimeSpan.FromSeconds(Math.Max(_options.TransactionQuarantineSeconds, _options.WriteQuarantineSeconds)).Ticks;
            var groups = remote.Keys.Union(tv.Seen.Keys).ToList();
            foreach (var group in groups)
            {
                var now = remote.TryGetValue(group, out var v) ? v : 0;
                var known = tv.Seen.TryGetValue(group, out var before);
                // First read of this tenant: nothing is cached for it yet (every cache use syncs first), so nothing moved.
                if (tv.EverRead && (known ? before : 0) != now) moved.Add(group);
                if (!known || before != now)
                {
                    // A group never bumped (no row) has nothing in flight. Otherwise: settled once its age passes the quarantine; an
                    // unknown age counts as "moved just now".
                    var ageTicks = withAge is not null && withAge.TryGetValue(group, out var gv) ? TimeSpan.FromMilliseconds(gv.AgeMs).Ticks : 0;
                    tv.SettledAtTicks[group] = remote.ContainsKey(group) ? nowTicks + Math.Max(0, quarantineTicks - ageTicks) : 0;
                }
                tv.Seen[group] = now;
            }
            // Versions were unreadable until now: groups cached meanwhile (non-security ones may be) could be stale — drop them all once.
            if (!tv.Available)
                moved.AddRange(_groups.Keys.Where(k => k.Tenant == tenantId).Select(k => k.Group));
            tv.EverRead = true;
            tv.Available = true;
            foreach (var group in groups)
            {
                if (_groups.TryGetValue((tenantId, group), out var gs))
                    lock (gs) gs.SharedVersion = tv.Seen.TryGetValue(group, out var sv) ? sv : 0;
            }
        }
        foreach (var group in moved.Distinct())
        {
            // Another instance's write: its transaction may still be open, so the group stays uncached for the transaction quarantine.
            _logger.LogDebug("Metadata cache group {Group} of tenant {TenantId} changed on another instance", group, tenantId);
            InvalidateLocal(tenantId, group, inTransaction: true);
        }
        return true;
    }

    private ConnectionMultiplexer? ConnectDistributed()
    {
        if (string.IsNullOrWhiteSpace(_distributedConn)) return null;
        try
        {
            var config = ConfigurationOptions.Parse(_distributedConn);
            config.AbortOnConnectFail = false;
            config.ConnectTimeout = Math.Max(200, _options.DistributedConnectTimeoutMs);
            config.SyncTimeout = Math.Max(200, _options.DistributedOperationTimeoutMs);
            config.AsyncTimeout = Math.Max(200, _options.DistributedOperationTimeoutMs);
            if (_provider == CacheProviderKind.AzureCache && !_distributedConn.Contains("ssl=", StringComparison.OrdinalIgnoreCase))
                config.Ssl = true;
            return ConnectionMultiplexer.Connect(config);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not connect to {Provider} metadata cache; falling back to L1 InMemory cache", _provider);
            return null;
        }
    }

    private string DistributedKey(long tenantId, string group, long sharedVersion, string key) =>
        $"{_options.KeyPrefix}{tenantId}:{group}:v{sharedVersion}:{key}";

    private async Task<T?> TryGetDistributedAsync<T>(long tenantId, string group, long sharedVersion, string key) where T : class
    {
        try
        {
            var mux = _redis.Value;
            if (mux is null || !mux.IsConnected) return null;
            var raw = await mux.GetDatabase().StringGetAsync(DistributedKey(tenantId, group, sharedVersion, key));
            if (raw.IsNullOrEmpty) return null;
            return JsonSerializer.Deserialize<T>(raw.ToString(), RedisJsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Distributed cache read skipped for {Group}/{Key}", group, key);
            return null;
        }
    }

    private async Task TrySetDistributedAsync<T>(long tenantId, string group, long sharedVersion, string key, T value) where T : class
    {
        try
        {
            var mux = _redis.Value;
            if (mux is null || !mux.IsConnected) return;
            var json = JsonSerializer.Serialize(value, RedisJsonOptions);
            await mux.GetDatabase().StringSetAsync(
                DistributedKey(tenantId, group, sharedVersion, key),
                json,
                TimeSpan.FromHours(Math.Max(1, _options.TtlHours)));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Distributed cache write skipped for {Group}/{Key}", group, key);
        }
    }

    public void Dispose()
    {
        if (_redis.IsValueCreated)
            _redis.Value?.Dispose();
    }
}
