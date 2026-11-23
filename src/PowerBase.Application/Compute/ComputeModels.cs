using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace PowerBase.Application.Compute;

/// <summary>Why a ComputeOutbox row was written (meta.ComputeOutbox.Reason).</summary>
public enum ComputeChangeReason : byte
{
    Create = 0,
    Update = 1,
    Delete = 2,
    MassUpdate = 3,
    /// <summary>Children were linked to (new) parents in the background.</summary>
    Relink = 4,
    /// <summary>"Recompute this whole table": an import, a copy, a bulk value rewrite. No per-record rows.</summary>
    TableWide = 5
}

/// <summary>
/// One change the compute worker must catch up with. Exactly one of <see cref="RecordId"/> / <see cref="RecordPublicId"/> is set for a
/// single-record change (a write path knows one or the other; the worker resolves the other). Both null = table-level work.
/// </summary>
/// <param name="ChangedFids">The fids the change touched; null = unknown (treat as all).</param>
/// <param name="OldRefs">For a Reference field whose value changed: fid -> the OLD parent record Id (null = it had none). Without this the
/// old parent's Summary would never be recomputed.</param>
public sealed record ComputeChange(
    long TableId,
    ComputeChangeReason Reason,
    long? RecordId = null,
    Guid? RecordPublicId = null,
    IReadOnlyCollection<int>? ChangedFids = null,
    IReadOnlyDictionary<int, long?>? OldRefs = null)
{
    public bool IsTableWide => RecordId is null && RecordPublicId is null;

    public static ComputeChange TableWide(long tableId) => new(tableId, ComputeChangeReason.TableWide);
}

/// <summary>A ComputeOutbox row the relay claimed.</summary>
public sealed record ComputeOutboxEntry(long Id, long TableId, int Attempts);

public sealed record ComputeOutboxStats(long Pending, long Parked, DateTime? OldestPendingUtc);

/// <summary>"Table X has work": what the compute queue carries. It holds no row data; the worker reads the rows from ComputeOutbox,
/// so one message drains many changes.</summary>
public sealed record ComputeQueueMessage(long TenantId, long TableId)
{
    /// <summary>One worker owns one table of one tenant at a time, so two workers never fight over the same rows.</summary>
    public string SessionId => $"{TenantId}:{TableId}";
}

/// <summary>JSON shapes of the ComputeOutbox columns, in one place so the writer, the worker and the tests agree.</summary>
public static class ComputeOutboxJson
{
    /// <summary>[105,106] (sorted, distinct); null when there is nothing to say.</summary>
    public static string? SerializeFids(IEnumerable<int>? fids)
    {
        if (fids is null) return null;
        var list = fids.Distinct().OrderBy(f => f).ToList();
        return list.Count == 0 ? null : JsonSerializer.Serialize(list);
    }

    /// <summary>{"7":42,"9":null} (fid -> old parent Id); null when empty.</summary>
    public static string? SerializeOldRefs(IReadOnlyDictionary<int, long?>? oldRefs)
    {
        if (oldRefs is null || oldRefs.Count == 0) return null;
        var sorted = oldRefs.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
        return JsonSerializer.Serialize(sorted);
    }

    public static IReadOnlyList<int> ParseFids(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<int>>(json) ?? [];

    public static IReadOnlyDictionary<int, long?> ParseOldRefs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<int, long?>();
        var raw = JsonSerializer.Deserialize<Dictionary<string, long?>>(json) ?? [];
        return raw.ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value);
    }
}

/// <summary>Plans what the relay publishes for the rows it claimed.</summary>
public static class ComputeOutboxPlanner
{
    /// <summary>A write touching more records than this queues ONE "recompute this table" row instead of one row per record
    /// (a 1 lakh row import must not write 1 lakh outbox rows).</summary>
    public const int TableWideThreshold = 1000;

    /// <summary>
    /// Keeps per-record changes as they are, unless there are more than <paramref name="threshold"/> for one table: those become a
    /// single table-wide change. Changes of other tables, and changes that are already table-wide, are kept.
    /// </summary>
    public static IReadOnlyList<ComputeChange> CollapseLargeBatch(IReadOnlyCollection<ComputeChange> changes, int threshold = TableWideThreshold)
    {
        var tooLarge = changes.Where(c => !c.IsTableWide).GroupBy(c => c.TableId).Where(g => g.Count() > threshold).Select(g => g.Key).ToHashSet();
        if (tooLarge.Count == 0) return changes as IReadOnlyList<ComputeChange> ?? changes.ToList();

        var result = changes.Where(c => c.IsTableWide || !tooLarge.Contains(c.TableId)).ToList();
        foreach (var tableId in tooLarge.OrderBy(t => t))
            if (!result.Any(c => c.IsTableWide && c.TableId == tableId))
                result.Add(ComputeChange.TableWide(tableId));
        return result;
    }

    /// <summary>One message per table, however many rows are waiting: the worker drains them all in one go.</summary>
    public static IReadOnlyList<ComputeQueueMessage> ToMessages(long tenantId, IEnumerable<ComputeOutboxEntry> entries) =>
        entries.Select(e => e.TableId).Distinct().OrderBy(t => t).Select(t => new ComputeQueueMessage(tenantId, t)).ToList();
}
