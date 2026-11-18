using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Infrastructure.Services;

public sealed class MetadataCache : IMetadataCache
{
    private sealed class GroupState
    {
        public CancellationTokenSource Cts = new();
        public long Version;
        public long QuarantineUntilTicks;
    }

    private readonly IMemoryCache _cache;
    private readonly MetadataCacheOptions _options;
    private readonly ILogger<MetadataCache> _logger;
    private readonly ConcurrentDictionary<(long Tenant, string Group), GroupState> _groups = new();

    public MetadataCache(IMemoryCache cache, IOptions<MetadataCacheOptions> options, ILogger<MetadataCache> logger)
    {
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<T> GetOrAddAsync<T>(long tenantId, string group, string key, Func<Task<T>> factory) where T : class
    {
        if (!_options.Enabled) return await factory();

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

        var value = await factory();
        _logger.LogDebug("Metadata cache miss {Key}", cacheKey);
        if (value is null || quarantined) return value;

        lock (state)
        {
            // A write started while the factory ran: what it read may already be out of date.
            if (state.Version != version) return value;
        }

        // The entry hangs on the group's token, so a write that lands right after the check above still evicts it.
        _cache.Set(cacheKey, value, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(Math.Max(1, _options.TtlHours)) }
            .AddExpirationToken(new CancellationChangeToken(token)));
        return value;
    }

    public void Invalidate(long tenantId, string group, bool inTransaction = false)
    {
        if (!_options.Enabled) return;
        var state = _groups.GetOrAdd((tenantId, group), _ => new GroupState());
        var quarantine = TimeSpan.FromSeconds(inTransaction ? _options.TransactionQuarantineSeconds : _options.WriteQuarantineSeconds);
        CancellationTokenSource old;
        lock (state)
        {
            state.Version++;
            state.QuarantineUntilTicks = (DateTime.UtcNow + quarantine).Ticks;
            old = state.Cts;
            state.Cts = new CancellationTokenSource();
        }
        old.Cancel();
    }

    public void FlushTenant(long tenantId)
    {
        foreach (var (tenant, group) in _groups.Keys)
            if (tenant == tenantId) Invalidate(tenantId, group);
    }
}
