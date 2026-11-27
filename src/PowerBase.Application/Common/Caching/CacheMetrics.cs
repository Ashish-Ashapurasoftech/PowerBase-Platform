using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace PowerBase.Application.Common.Caching;

/// <summary>
/// Doc 34 §4 / doc 23 §3: the numbers that decide how big Redis must be and whether a result cache is worth building. Published on the
/// <see cref="MeterName"/> meter (Application Insights picks it up through OpenTelemetry) and summed in-process for a log line every
/// <see cref="SummaryInterval"/>, so they can be read without a metrics backend too. Counting only: nothing here changes a result.
/// </summary>
public static class CacheMetrics
{
    public const string MeterName = "PowerBase.Cache";
    public static readonly TimeSpan SummaryInterval = TimeSpan.FromMinutes(5);

    /// <summary>Where a metadata lookup was answered.</summary>
    public static class Source
    {
        public const string L1 = "l1";
        public const string L2 = "l2";
        public const string Sql = "sql";
        /// <summary>Not cached on purpose: cache off, a quarantined group, or a security group whose versions could not be read.</summary>
        public const string Bypass = "bypass";
        /// <summary>Waited for another request's load of the same key (single-flight).</summary>
        public const string Shared = "shared";
    }

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> LookupCounter = Meter.CreateCounter<long>("pb.metadata_cache.lookups", description: "Metadata cache lookups by group and source");
    private static readonly Counter<long> L2ErrorCounter = Meter.CreateCounter<long>("pb.metadata_cache.l2_errors", description: "Redis L2 failures (each opens the 30 s circuit)");
    private static readonly Counter<long> L2BytesCounter = Meter.CreateCounter<long>("pb.metadata_cache.l2_bytes_written", unit: "By", description: "Bytes written to Redis L2 by group");
    private static readonly Counter<long> ReportRunCounter = Meter.CreateCounter<long>("pb.report_runs", description: "Report runs by type, and whether the same question was asked within 60 s");

    private static readonly ConcurrentDictionary<string, long> Totals = new(StringComparer.Ordinal);
    private static long _lastSummaryTicks = DateTime.UtcNow.Ticks;

    public static void Lookup(string group, string source)
    {
        LookupCounter.Add(1, new KeyValuePair<string, object?>("group", group), new KeyValuePair<string, object?>("source", source));
        Totals.AddOrUpdate($"lookup:{group}:{source}", 1, (_, n) => n + 1);
    }

    public static void L2Error() { L2ErrorCounter.Add(1); Totals.AddOrUpdate("l2:errors", 1, (_, n) => n + 1); }

    public static void L2BytesWritten(string group, long bytes)
    {
        L2BytesCounter.Add(bytes, new KeyValuePair<string, object?>("group", group));
        Totals.AddOrUpdate($"l2bytes:{group}", bytes, (_, n) => n + bytes);
        Totals.AddOrUpdate($"l2writes:{group}", 1, (_, n) => n + 1);
    }

    public static void ReportRun(string reportType, bool encrypted, bool repeat)
    {
        ReportRunCounter.Add(1, new KeyValuePair<string, object?>("type", reportType),
            new KeyValuePair<string, object?>("encrypted", encrypted), new KeyValuePair<string, object?>("repeat", repeat));
        Totals.AddOrUpdate($"report:{reportType}:{(encrypted ? "enc" : "plain")}:{(repeat ? "repeat" : "new")}", 1, (_, n) => n + 1);
    }

    private static readonly Counter<long> ResultCacheCounter = Meter.CreateCounter<long>("pb.result_cache.lookups", description: "Report result cache: hit, miss, or why it was skipped");
    private static readonly Counter<long> ResultCacheBytesCounter = Meter.CreateCounter<long>("pb.result_cache.bytes_written", unit: "By", description: "Bytes of report answers written to Redis");

    /// <summary>Report result cache outcome: hit, miss, skip-versions, skip-write-open, skip-down, error.</summary>
    public static void ResultCache(string outcome)
    {
        ResultCacheCounter.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        Totals.AddOrUpdate($"result:{outcome}", 1, (_, n) => n + 1);
    }

    public static void ResultCacheBytes(long bytes)
    {
        ResultCacheBytesCounter.Add(bytes);
        Totals.AddOrUpdate("result:bytes", bytes, (_, n) => n + bytes);
    }

    /// <summary>The totals since the process started (key → count or bytes). Sorted for a stable log line.</summary>
    public static IReadOnlyList<KeyValuePair<string, long>> Snapshot() => Totals.OrderBy(t => t.Key, StringComparer.Ordinal).ToList();

    /// <summary>True at most once per <see cref="SummaryInterval"/> across the process: the caller then logs <see cref="Snapshot"/>.</summary>
    public static bool SummaryDue()
    {
        var last = Interlocked.Read(ref _lastSummaryTicks);
        var now = DateTime.UtcNow.Ticks;
        return now - last >= SummaryInterval.Ticks && Interlocked.CompareExchange(ref _lastSummaryTicks, now, last) == last;
    }

}

/// <summary>
/// Measure-only probe for doc 23 §3 ("counts / summary results repeat within 60 s for > 30 % of runs"): remembers a hash of each
/// report question (tenant, user, report definition, every run parameter) for 60 s and tells whether it was asked already. Holds hashes
/// only, never results; bounded (cleared when it holds more than <see cref="MaxEntries"/>).
/// </summary>
public static class ReportRepeatProbe
{
    public const int MaxEntries = 50_000;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    private static readonly ConcurrentDictionary<string, long> Seen = new(StringComparer.Ordinal);

    public static bool IsRepeat(string questionHash)
    {
        var now = DateTime.UtcNow.Ticks;
        var repeat = Seen.TryGetValue(questionHash, out var at) && now - at < Window.Ticks;
        if (Seen.Count >= MaxEntries) Seen.Clear();
        Seen[questionHash] = now;
        return repeat;
    }

}
