using System.Collections.Concurrent;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Search;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Pipelines;

/// <summary>Whether a search the planner says the index can narrow down should actually use it right now (see <see cref="PipelineAiSearchGate"/>).</summary>
public sealed record PipelineAiSearchGateResult(bool UseAiSearch, string? SqlReason)
{
    public static PipelineAiSearchGateResult Yes { get; } = new(true, null);
}

/// <summary>
/// The checks that keep an answer from the index from missing a record, made once before a search starts (never while one is being resumed):
///   • a field the filter reads changed recently — a field just switched on for search is still being copied into the index for the
///     records that were already there, so the index would not have its values yet;
///   • the table is small — SQL finds the records as quickly, and without the index's lag;
///   • the index holds fewer documents than the table has records (allowing for the records changed within the index-lag window, which
///     every index-answered search reads from SQL anyway) — documents are missing, so the index cannot be trusted for this table.
/// Any check that cannot be made counts as "use SQL".
/// </summary>
public static class PipelineAiSearchGate
{
    /// <summary>More recently changed records than this (a bulk load is running) and the counts are not compared: SQL answers.</summary>
    public const int MaxRecentTolerated = 20000;

    private const int RecentPageSize = 1000;

    private sealed record Counts(DateTime AtUtc, long Sql, long Index, long Recent);

    private static readonly ConcurrentDictionary<(long Tenant, long Table, bool Compared), Counts> Cache = new();

    public static async Task<PipelineAiSearchGateResult> EvaluateAsync(
        AppTable table, IReadOnlyList<AppField> fields, PipelineAiSearchPlan plan, IAzureSearchService azure, long tenantId,
        IRecordRepository records, PipelineExecutionOptions options, CancellationToken ct, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;

        if (options.AiSearchFieldSettleHours > 0 && plan.FieldIds is { Count: > 0 })
        {
            var since = now.AddHours(-options.AiSearchFieldSettleHours);
            foreach (var fid in plan.FieldIds)
            {
                var field = fields.FirstOrDefault(f => f.Fid.HasValue && f.Fid.Value == fid && !f.IsDeleted);
                if (field != null && (field.ModifiedOn ?? field.CreatedOn) >= since)
                    return No($"field '{(string.IsNullOrWhiteSpace(field.Label) ? field.Name : field.Label)}' changed within the last {options.AiSearchFieldSettleHours} hours, so the index may not hold its values yet");
            }
        }

        var compare = options.AiSearchCountGuard;
        if (options.AiSearchMinTableRows <= 0 && !compare) return PipelineAiSearchGateResult.Yes;

        try
        {
            var counts = await CountsAsync(table, fields, azure, tenantId, records, options, compare, now, ct);
            if (options.AiSearchMinTableRows > 0 && counts.Sql < options.AiSearchMinTableRows)
                return No($"the table has {counts.Sql} records, fewer than {options.AiSearchMinTableRows}: SQL reads it as quickly");
            if (compare)
            {
                if (counts.Recent > MaxRecentTolerated)
                    return No($"more than {MaxRecentTolerated} records changed within the last {options.AiSearchIndexLagSeconds} seconds");
                if (counts.Index + counts.Recent < counts.Sql)
                    return No($"the index holds {counts.Index} documents for {counts.Sql} records ({counts.Recent} changed recently), so it is missing some");
            }
            return PipelineAiSearchGateResult.Yes;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return No($"the table and index counts could not be checked ({ex.Message})");
        }
    }

    private static async Task<Counts> CountsAsync(
        AppTable table, IReadOnlyList<AppField> fields, IAzureSearchService azure, long tenantId, IRecordRepository records,
        PipelineExecutionOptions options, bool compare, DateTime now, CancellationToken ct)
    {
        var key = (tenantId, table.Id, compare);
        if (options.AiSearchGuardCacheSeconds > 0 && Cache.TryGetValue(key, out var cached)
            && (now - cached.AtUtc).TotalSeconds < options.AiSearchGuardCacheSeconds)
            return cached;

        var sql = await records.CountAsync(table, fields, null, null, ct);
        long index = 0, recent = 0;
        if (compare)
        {
            var targetIndex = SearchSlotResolver.IsWideTable(fields) ? SearchSlotResolver.GetTableIndexName(table.Id) : null;
            var (_, total) = await azure.SearchRecordsPagedAsync(tenantId, table.Id, null, null, 1, 1, null, targetIndex, ct);
            index = total;
            recent = await RecentlyChangedAsync(table, records, options, now, ct);
        }

        var counts = new Counts(now, sql, index, recent);
        if (options.AiSearchGuardCacheSeconds > 0) Cache[key] = counts;
        return counts;
    }

    /// <summary>How many records changed within the index-lag window (counted up to <see cref="MaxRecentTolerated"/> + 1).</summary>
    private static async Task<long> RecentlyChangedAsync(AppTable table, IRecordRepository records, PipelineExecutionOptions options, DateTime now, CancellationToken ct)
    {
        if (options.AiSearchIndexLagSeconds <= 0) return 0;
        var since = now.AddSeconds(-options.AiSearchIndexLagSeconds);
        long count = 0;
        for (var afterId = 0L; ;)
        {
            var changed = await records.ListRecordsChangedSinceAsync(table, since, afterId, long.MaxValue, RecentPageSize, ct);
            count += changed.Count;
            if (changed.Count < RecentPageSize || count > MaxRecentTolerated) return count;
            afterId = changed[^1].Id;
        }
    }

    private static PipelineAiSearchGateResult No(string reason) => new(false, reason);
}
