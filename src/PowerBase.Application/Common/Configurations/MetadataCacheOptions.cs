namespace PowerBase.Application.Common.Configurations;

/// <summary>Settings of the metadata cache, from the "MetadataCache" section of the configuration. Every value has a default, so the section is optional.</summary>
public class MetadataCacheOptions
{
    public const string SectionName = "MetadataCache";

    /// <summary>Kill switch: false makes every read go to the database, exactly as before the cache existed.</summary>
    public bool Enabled { get; set; } = true;

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
    /// <c>MetadataCache:Redis:ConnectionString</c> is set.</summary>
    public RedisOptions Redis { get; set; } = new();

    /// <summary>Longest wait for an L2 read before answering from SQL (and skipping L2 for 30 s).</summary>
    public int L2TimeoutMs { get; set; } = 100;

    /// <summary>Prefix of every L2 key (one Redis can serve several environments).</summary>
    public string L2KeyPrefix { get; set; } = "pb";

    public sealed class RedisOptions
    {
        /// <summary>StackExchange.Redis connection string (local development: "localhost:6379"; Azure: host:10000, key from Key Vault, ssl=True). Empty = no L2.</summary>
        public string? ConnectionString { get; set; }
        /// <summary>Prefix inside Redis. Default "powerbase:{environment}:", so environments sharing one Redis never meet.</summary>
        public string? InstanceName { get; set; }
    }
}
