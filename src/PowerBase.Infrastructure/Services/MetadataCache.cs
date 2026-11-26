using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using PowerBase.Application.Common.Caching;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Infrastructure.Services;

/// <summary>
/// Process-local metadata cache whose invalidation reaches every instance (ARC-01). Each write bumps the group's shared version in
/// SQL (<see cref="ICacheVersionStore"/>) besides dropping it here; every instance compares those versions with what it last saw —
/// once per request (<see cref="CacheVersionScope"/>), at most every <see cref="MetadataCacheOptions.VersionCheckIntervalMs"/> in
/// background work — and drops a group whose version moved, keeping it out of the cache for the write's quarantine. Security groups
/// (<see cref="MetadataCacheGroups.IsSecurityRelevant"/>) are never served from the cache when the versions cannot be read.
/// </summary>
public sealed class MetadataCache : IMetadataCache
{
    private sealed class GroupState
    {
        public CancellationTokenSource Cts = new();
        public long Version;
        public long QuarantineUntilTicks;
    }

    /// <summary>What this process last saw of a tenant's shared versions.</summary>
    private sealed class TenantVersions
    {
        public readonly Dictionary<string, long> Seen = new(StringComparer.Ordinal);
        public long CheckedAtTicks;
        public bool Available;
        public bool EverRead;
    }

    private static readonly string[] TenantGroups =
        [MetadataCacheGroups.Fields, MetadataCacheGroups.Tables, MetadataCacheGroups.Apps, MetadataCacheGroups.Access, MetadataCacheGroups.Ids];
    private static readonly string[] GlobalGroups = [MetadataCacheGroups.UserPermissions, MetadataCacheGroups.Sessions];

    private readonly IMemoryCache _cache;
    private readonly MetadataCacheOptions _options;
    private readonly ILogger<MetadataCache> _logger;
    private readonly ICacheVersionStore? _versions;
    private readonly ConcurrentDictionary<(long Tenant, string Group), GroupState> _groups = new();
    private readonly ConcurrentDictionary<long, TenantVersions> _tenantVersions = new();

    /// <summary>Loads in flight by cache key: concurrent misses of one key share one factory call (no stampede at morning start-up).</summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> _inflight = new(StringComparer.Ordinal);

    public MetadataCache(IMemoryCache cache, IOptions<MetadataCacheOptions> options, ILogger<MetadataCache> logger, ICacheVersionStore? versions = null)
    {
        _options = options.Value;
        // Bounded memory: its own cache with a size limit (entries of 1 unit each). Not the shared IMemoryCache, whose other users set
        // no entry sizes (a size-limited cache refuses those).
        _cache = _options.SizeLimitEntries > 0
            ? new MemoryCache(new MemoryCacheOptions { SizeLimit = _options.SizeLimitEntries, CompactionPercentage = 0.2 })
            : cache;
        _logger = logger;
        _versions = _options.SharedVersions ? versions : null;
    }

    public async Task<T> GetOrAddAsync<T>(long tenantId, string group, string key, Func<Task<T>> factory) where T : class
    {
        if (!_options.Enabled) return await factory();

        // Other instances' writes first: a moved version drops the group here before it is read. Without readable versions a
        // security group is answered from SQL every time (fail closed); other groups keep the local behaviour.
        if (_versions is not null && !await SyncAsync(tenantId) && MetadataCacheGroups.IsSecurityRelevant(group))
            return await factory();

        var state = _groups.GetOrAdd((tenantId, group), _ => new GroupState());
        var cacheKey = $"mc:{tenantId}:{group}:{key}";
        if (_cache.TryGetValue(cacheKey, out T? hit) && hit is not null)
        {
            _logger.LogDebug("Metadata cache hit {Key}", cacheKey);
            return hit;
        }

        long version;
        CancellationToken token;
        bool quarantined;
        lock (state)
        {
            version = state.Version;
            token = state.Cts.Token;
            quarantined = DateTime.UtcNow.Ticks < state.QuarantineUntilTicks;
        }

        T? value;
        if (quarantined)
            value = await factory();   // not stored anyway: no point sharing the load
        else
        {
            var load = _inflight.GetOrAdd(cacheKey, _ => new Lazy<Task<object?>>(async () => await factory()));
            try { value = (T?)await load.Value; }
            finally { _inflight.TryRemove(new KeyValuePair<string, Lazy<Task<object?>>>(cacheKey, load)); }
        }
        _logger.LogDebug("Metadata cache miss {Key}", cacheKey);
        if (value is null || quarantined) return value;

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

    public void Invalidate(long tenantId, string group, bool inTransaction = false)
    {
        if (!_options.Enabled) return;
        InvalidateLocal(tenantId, group, inTransaction);
        // Every other instance: the shared version moves now (before the write), so they stop caching the group on their next request.
        _versions?.Bump(tenantId, group);
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

        var remote = await _versions!.ReadAsync(tenantId);
        var moved = new List<string>();
        lock (tv)
        {
            tv.CheckedAtTicks = DateTime.UtcNow.Ticks;
            if (remote is null)
            {
                tv.Available = false;
                return false;
            }
            var groups = remote.Keys.Union(tv.Seen.Keys).ToList();
            foreach (var group in groups)
            {
                var now = remote.TryGetValue(group, out var v) ? v : 0;
                // First read of this tenant: nothing is cached for it yet (every cache use syncs first), so nothing moved.
                if (tv.EverRead && (tv.Seen.TryGetValue(group, out var before) ? before : 0) != now) moved.Add(group);
                tv.Seen[group] = now;
            }
            // Versions were unreadable until now: groups cached meanwhile (non-security ones may be) could be stale — drop them all once.
            if (!tv.Available)
                moved.AddRange(_groups.Keys.Where(k => k.Tenant == tenantId).Select(k => k.Group));
            tv.EverRead = true;
            tv.Available = true;
        }
        foreach (var group in moved.Distinct())
        {
            // Another instance's write: its transaction may still be open, so the group stays uncached for the transaction quarantine.
            _logger.LogDebug("Metadata cache group {Group} of tenant {TenantId} changed on another instance", group, tenantId);
            InvalidateLocal(tenantId, group, inTransaction: true);
        }
        return true;
    }
}
