namespace PowerBase.Application.Common.Configurations;

public enum CacheProviderKind
{
    InMemory = 0,
    Redis = 1,
    AzureCache = 2,
}

/// <summary>Settings of the metadata cache, from the "MetadataCache" section of the configuration. Every value has a default, so the section is optional.</summary>
public class MetadataCacheOptions
{
    public const string SectionName = "MetadataCache";

    /// <summary>Kill switch: false makes every read go to the database, exactly as before the cache existed.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whether process-local L1 memory caching is used. Set to false in multi-instance deployments that should read strictly from shared Redis / Azure Cache and SQL without per-instance L1 copies.</summary>
    public bool UseInMemoryCache { get; set; } = true;

    /// <summary>Cache backing store: "InMemory" (default, process-local), "Redis" (L1 memory + Redis L2), or "AzureCache" (L1 memory + Azure Cache for Redis over TLS).</summary>
    public string Provider { get; set; } = nameof(CacheProviderKind.InMemory);

    /// <summary>Connection string when <see cref="Provider"/> is "Redis".</summary>
    public string? RedisConnectionString { get; set; }

    /// <summary>Connection string when <see cref="Provider"/> is "AzureCache" (Azure Cache for Redis / Azure Managed Redis).</summary>
    public string? AzureCacheConnectionString { get; set; }

    /// <summary>Key prefix in Redis / Azure Cache so environments or apps sharing one cache never collide.</summary>
    public string KeyPrefix { get; set; } = "pb:meta:";

    /// <summary>Connect timeout in ms for Redis / Azure Cache (falls back to L1/SQL on failure).</summary>
    public int DistributedConnectTimeoutMs { get; set; } = 1000;

    /// <summary>Operation timeout in ms for Redis / Azure Cache reads/writes.</summary>
    public int DistributedOperationTimeoutMs { get; set; } = 1000;

    /// <summary>Backstop only — entries are normally dropped by the write that changes them.</summary>
    public int TtlHours { get; set; } = 24;

    /// <summary>How long a group stays uncached after a write, so a read that was already running cannot store the old data.</summary>
    public int WriteQuarantineSeconds { get; set; } = 5;

    /// <summary>Same, for a write inside a database transaction, which becomes visible only when the caller commits.</summary>
    public int TransactionQuarantineSeconds { get; set; } = 30;

    /// <summary>Shared versions in SQL (meta.CacheVersion) so a write on one API instance invalidates every instance (ARC-01). Required
    /// before running more than one instance. Off = the old process-local behaviour.</summary>
    public bool SharedVersions { get; set; } = true;

    /// <summary>Outside a request (background loops), how often the shared versions are re-read. Requests read them once each.</summary>
    public int VersionCheckIntervalMs { get; set; } = 1000;

    /// <summary>Most entries kept in memory (each list of fields / tables / permissions is one entry); 0 = unbounded. Least recently used
    /// entries are compacted away first.</summary>
    public long SizeLimitEntries { get; set; } = 100_000;

    /// <summary>Optional L2 (Azure Managed Redis), shared by every instance, for non-security groups only. Off unless
    /// <c>MetadataCache:Redis:ConnectionString</c>/<c>Host</c> or <c>MetadataCache:Provider</c> is set.</summary>
    public RedisOptions Redis { get; set; } = new();

    /// <summary>Longest wait for an L2 read before answering from SQL (and skipping L2 for 30 s).</summary>
    public int L2TimeoutMs { get; set; } = 100;

    /// <summary>Prefix of every L2 key (one Redis can serve several environments).</summary>
    public string L2KeyPrefix { get; set; } = "pb";

    public sealed class RedisOptions
    {
        /// <summary>StackExchange.Redis connection string (local development: "localhost:6379"; Azure: host:10000, key from Key Vault, ssl=True). Empty = no L2.</summary>
        public string? ConnectionString { get; set; }
        /// <summary>Azure Managed Redis "host:port" (e.g. "pb-cache.westus2.redis.azure.net:10000"): Entra ID with the app's managed identity,
        /// TLS, no access key. Wins over ConnectionString.</summary>
        public string? Host { get; set; }
        /// <summary>Client id of a user-assigned managed identity for Host; empty = the system-assigned one.</summary>
        public string? ManagedIdentityClientId { get; set; }
        /// <summary>Prefix inside Redis. Default "powerbase:{environment}:", so environments sharing one Redis never meet.</summary>
        public string? InstanceName { get; set; }
    }

    public CacheProviderKind ResolveProvider() =>
        Provider?.Trim() switch
        {
            var p when string.Equals(p, nameof(CacheProviderKind.Redis), StringComparison.OrdinalIgnoreCase) => CacheProviderKind.Redis,
            var p when string.Equals(p, nameof(CacheProviderKind.AzureCache), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p, "AzureRedis", StringComparison.OrdinalIgnoreCase) => CacheProviderKind.AzureCache,
            _ when !string.IsNullOrWhiteSpace(Redis.Host) => CacheProviderKind.AzureCache,
            _ when !string.IsNullOrWhiteSpace(Redis.ConnectionString) => CacheProviderKind.Redis,
            _ => CacheProviderKind.InMemory,
        };

    public string? ResolveDistributedConnectionString() =>
        ResolveProvider() switch
        {
            CacheProviderKind.AzureCache => !string.IsNullOrWhiteSpace(AzureCacheConnectionString)
                ? AzureCacheConnectionString
                : !string.IsNullOrWhiteSpace(RedisConnectionString) ? RedisConnectionString : Redis.Host ?? Redis.ConnectionString,
            CacheProviderKind.Redis => !string.IsNullOrWhiteSpace(RedisConnectionString)
                ? RedisConnectionString
                : !string.IsNullOrWhiteSpace(AzureCacheConnectionString) ? AzureCacheConnectionString : Redis.ConnectionString,
            _ => null,
        };
}
