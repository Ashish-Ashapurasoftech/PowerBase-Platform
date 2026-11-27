using System.Diagnostics;

namespace PowerBase.Application.Common.Caching;

/// <summary>
/// What the metadata cache did for one HTTP request: how often and how long it read the shared versions (SQL), Redis L2 and SQL
/// behind a cache miss. Sent in the <c>Server-Timing</c> header (cache-sync, cache-l2, cache-sql) so a slow request shows whether
/// the time went to version checks, Redis or the database. Counting only: nothing here changes a result.
/// </summary>
public sealed class CacheRequestStats
{
    private static readonly AsyncLocal<CacheRequestStats?> CurrentStats = new();

    private long _syncCount, _syncTicks;
    private long _l1Hits;
    private long _l2Hits, _l2Misses, _l2Ticks;
    private long _sqlCount, _sqlTicks;
    private long _bypassCount;

    public static CacheRequestStats? Current => CurrentStats.Value;

    /// <summary>Starts counting for the current async flow (the request); tasks it starts count into the same instance.</summary>
    public static CacheRequestStats Begin()
    {
        var stats = new CacheRequestStats();
        CurrentStats.Value = stats;
        return stats;
    }

    public long SyncCount => Interlocked.Read(ref _syncCount);
    public TimeSpan SyncTime => Elapsed(Interlocked.Read(ref _syncTicks));
    public long L1Hits => Interlocked.Read(ref _l1Hits);
    public long L2Hits => Interlocked.Read(ref _l2Hits);
    public long L2Misses => Interlocked.Read(ref _l2Misses);
    public TimeSpan L2Time => Elapsed(Interlocked.Read(ref _l2Ticks));
    public long SqlCount => Interlocked.Read(ref _sqlCount);
    public TimeSpan SqlTime => Elapsed(Interlocked.Read(ref _sqlTicks));
    public long BypassCount => Interlocked.Read(ref _bypassCount);

    public static void Sync(long startTimestamp) => Add(c => { Interlocked.Increment(ref c._syncCount); Interlocked.Add(ref c._syncTicks, Stopwatch.GetTimestamp() - startTimestamp); });
    public static void L1Hit() => Add(c => Interlocked.Increment(ref c._l1Hits));
    /// <summary>One Redis read finished (hit, miss, timeout or error): its time.</summary>
    public static void L2Read(long startTimestamp) => Add(c => Interlocked.Add(ref c._l2Ticks, Stopwatch.GetTimestamp() - startTimestamp));
    /// <summary>A Redis read that answered: whether it held a usable entry.</summary>
    public static void L2Outcome(bool hit) => Add(c => Interlocked.Increment(ref hit ? ref c._l2Hits : ref c._l2Misses));
    public static void Sql(long startTimestamp) => Add(c => { Interlocked.Increment(ref c._sqlCount); Interlocked.Add(ref c._sqlTicks, Stopwatch.GetTimestamp() - startTimestamp); });
    public static void Bypass() => Add(c => Interlocked.Increment(ref c._bypassCount));

    private long _tenantConnections, _controlConnections;
    /// <summary>SQL connections the request created (each repository call opens its own, so this is close to its SQL round trips),
    /// cached or not: the number to compare before and after a caching change.</summary>
    public long TenantConnections => Interlocked.Read(ref _tenantConnections);
    public long ControlConnections => Interlocked.Read(ref _controlConnections);
    public static void TenantConnection() => Add(c => Interlocked.Increment(ref c._tenantConnections));
    public static void ControlConnection() => Add(c => Interlocked.Increment(ref c._controlConnections));

    private static void Add(Action<CacheRequestStats> update)
    {
        if (CurrentStats.Value is { } stats) update(stats);
    }

    private static TimeSpan Elapsed(long stopwatchTicks) => TimeSpan.FromSeconds(stopwatchTicks / (double)Stopwatch.Frequency);
}
