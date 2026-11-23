using System.Globalization;
using Microsoft.Extensions.Configuration;
using PowerBase.Application.Compute;

namespace PowerBase.Infrastructure.Compute;

/// <summary>
/// Reads the "Compute" and "ReadReplica" config sections into their options. Done by hand (no Binder package): every value that is
/// missing or unparsable keeps its default, which is the switched-off behaviour, so a deployment without the sections changes nothing.
/// </summary>
public static class ComputeConfig
{
    public static ComputeOptions ReadCompute(IConfiguration configuration)
    {
        var s = configuration.GetSection(ComputeOptions.SectionName);
        var o = new ComputeOptions();
        o.Enabled = Bool(s["Enabled"], o.Enabled);
        o.SettingsCacheSeconds = Int(s["SettingsCacheSeconds"], o.SettingsCacheSeconds);
        o.QueueName = string.IsNullOrWhiteSpace(s["QueueName"]) ? o.QueueName : s["QueueName"]!;
        o.RelayIntervalSeconds = Int(s["RelayIntervalSeconds"], o.RelayIntervalSeconds);
        o.GraceSeconds = Int(s["GraceSeconds"], o.GraceSeconds);
        o.RetryAfterSeconds = Int(s["RetryAfterSeconds"], o.RetryAfterSeconds);
        o.MaxAttempts = Int(s["MaxAttempts"], o.MaxAttempts);
        o.BatchSize = Int(s["BatchSize"], o.BatchSize);
        o.TenantConcurrency = Int(s["TenantConcurrency"], o.TenantConcurrency);
        o.RepublishEnabled = Bool(s["RepublishEnabled"], o.RepublishEnabled);
        o.ObserveRetentionDays = Int(s["ObserveRetentionDays"], o.ObserveRetentionDays);
        o.SummaryBatchSize = Math.Clamp(Int(s["SummaryBatchSize"], o.SummaryBatchSize), 100, 5000);
        o.LookupBatchSize = Math.Clamp(Int(s["LookupBatchSize"], o.LookupBatchSize), 100, 5000);
        o.DrainBatchSize = Math.Clamp(Int(s["DrainBatchSize"], o.DrainBatchSize), 1, 5000);
        o.MaxSessionsPerDatabase = Math.Clamp(Int(s["MaxSessionsPerDatabase"], o.MaxSessionsPerDatabase), 1, 8);
        o.BackgroundSessionsPerDatabase = Math.Clamp(Int(s["BackgroundSessionsPerDatabase"], o.BackgroundSessionsPerDatabase), 1, 7);
        o.PriorityTables = string.IsNullOrWhiteSpace(s["PriorityTables"]) ? o.PriorityTables : s["PriorityTables"];
        o.PauseBetweenBatchesMs = Int(s["PauseBetweenBatchesMs"], o.PauseBetweenBatchesMs);
        o.GuardSlowPercent = Int(s["GuardSlowPercent"], o.GuardSlowPercent);
        o.GuardStopPercent = Int(s["GuardStopPercent"], o.GuardStopPercent);
        o.OffPeakStartHourUtc = SignedInt(s["OffPeakStartHourUtc"], o.OffPeakStartHourUtc);
        o.OffPeakEndHourUtc = SignedInt(s["OffPeakEndHourUtc"], o.OffPeakEndHourUtc);
        o.BackfillPollSeconds = Int(s["BackfillPollSeconds"], o.BackfillPollSeconds);
        o.ReconcileHourUtc = Int(s["ReconcileHourUtc"], o.ReconcileHourUtc);
        o.StoreAllCalculatedFields = Bool(s["StoreAllCalculatedFields"], o.StoreAllCalculatedFields);
        o.IndexesEnabled = Bool(s["IndexesEnabled"], o.IndexesEnabled);
        o.IndexSyncMinutes = Math.Max(1, Int(s["IndexSyncMinutes"], o.IndexSyncMinutes));
        return o;
    }

    public static ReadReplicaOptions ReadReplica(IConfiguration configuration)
    {
        var s = configuration.GetSection(ReadReplicaOptions.SectionName);
        var o = new ReadReplicaOptions();
        o.Enabled = Bool(s["Enabled"], o.Enabled);
        o.MaxLagSeconds = Int(s["MaxLagSeconds"], o.MaxLagSeconds);
        o.BackgroundMaxLagSeconds = Int(s["BackgroundMaxLagSeconds"], o.BackgroundMaxLagSeconds);
        o.LagCacheSeconds = Int(s["LagCacheSeconds"], o.LagCacheSeconds);
        o.HeartbeatIntervalSeconds = Int(s["HeartbeatIntervalSeconds"], o.HeartbeatIntervalSeconds);
        return o;
    }

    private static int SignedInt(string? raw, int fallback) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>True when <paramref name="utcNow"/> is inside the off-peak window (always, when no window is set). The window may wrap
    /// midnight (22 -> 5).</summary>
    public static bool IsOffPeak(ComputeOptions options, DateTime utcNow)
    {
        var start = options.OffPeakStartHourUtc;
        var end = options.OffPeakEndHourUtc;
        if (start is < 0 or > 23 || end is < 0 or > 24 || start == end) return true;
        var hour = utcNow.Hour;
        return start < end ? hour >= start && hour < end : hour >= start || hour < end;
    }

    private static bool Bool(string? raw, bool fallback) => bool.TryParse(raw, out var v) ? v : fallback;

    private static int Int(string? raw, int fallback) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : fallback;
}
