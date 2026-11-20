using System.Text.Json;

namespace PowerBase.Application.QbMigration;

// A relationship that matches the child to its parent by a key (not by Record ID#) normally finds the parent's Record ID in
// PowerBase, so the parents must be loaded first. When a table points at ITSELF by key (CFTSS Client Weeks: "PreviousWeekRef"
// holds the key of the previous week, another record of the same table), a record's parent can sit in a chunk that is not loaded
// yet, so PowerBase can't answer. QuickBase can: the keys and Record IDs are read from there once, and Record IDs are kept
// unchanged by the migration, so the link needs no load order. Nothing here writes to PowerBase or QuickBase.

/// <summary>Where the keys of a self-referencing table are read: its QuickBase table and key field.</summary>
public sealed record QbKeyIndexSpec(string QbTableId, int KeyFid);

public static class QbKeyIndex
{
    /// <summary>The key as the transformer reads a child's key value: a string as it is, anything else as its raw text.</summary>
    public static string? KeyText(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.GetRawText(),
        _ => null,
    };

    /// <summary>Key to Record ID# of every record of the table. For a duplicated key the highest Record ID# wins, like the
    /// PowerBase-side lookup (<c>ParentKeyMapAsync</c>), so both ways resolve a duplicate alike.</summary>
    public static async Task<IReadOnlyDictionary<string, long>> BuildAsync(IQuickBaseClient client, QbKeyIndexSpec spec, CancellationToken ct = default)
    {
        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        // Ascending Record ID#, so a later record with the same key replaces the earlier one.
        await foreach (var page in new QbKeysetReader(client).ReadAsync(spec.QbTableId, [spec.KeyFid], null, ct))
            foreach (var row in page)
                if (row.Values.TryGetValue(spec.KeyFid, out var v) && KeyText(v) is { Length: > 0 } key)
                    map[key] = row.RecordId;
        return map;
    }
}

/// <summary>Builds each key index once per worker process and shares it between chunks (a read of the whole table, so asking
/// per chunk would be far too slow).</summary>
public sealed class QbKeyIndexes(IQuickBaseClient client)
{
    private readonly object _gate = new();
    private readonly Dictionary<QbKeyIndexSpec, Task<IReadOnlyDictionary<string, long>>> _cache = [];

    public async Task<IReadOnlyDictionary<string, long>> GetAsync(QbKeyIndexSpec spec)
    {
        Task<IReadOnlyDictionary<string, long>> task;
        lock (_gate)
        {
            if (!_cache.TryGetValue(spec, out task!))
                // Shared by every chunk that needs it, so one caller's cancellation must not abandon it.
                _cache[spec] = task = QbKeyIndex.BuildAsync(client, spec, CancellationToken.None);
        }
        try
        {
            return await task;
        }
        catch
        {
            // A failed read is not kept: the next chunk (or the retry of this one) reads the table again.
            lock (_gate)
                if (_cache.TryGetValue(spec, out var cached) && ReferenceEquals(cached, task)) _cache.Remove(spec);
            throw;
        }
    }
}
