using PowerBase.Domain.Entities;

namespace PowerBase.Infrastructure.QbMigration;

/// <summary>The parent keys of a reference that is matched by a text key (a child holds "G-17", the parent record whose key field
/// is "G-17" gives the record id): every non-deleted parent's key and Record ID#, read from PowerBase once per worker process
/// and shared by all chunks. Matching them with one query per 1,000 keys per chunk scanned the whole parent each time (the key
/// column has no index and is cast to text): on CFTSS that took 45 s against 307,000 parents and every chunk timed out.
/// Parent tables load before their children (table tiers), so the map is complete when the first child chunk asks for it.</summary>
public sealed class QbmParentKeyCache(Func<long, AppField, long, long, CancellationToken, Task<IReadOnlyDictionary<string, long>>> read)
{
    private readonly object _gate = new();
    private readonly Dictionary<(long Table, long Field), Task<IReadOnlyDictionary<string, long>>> _maps = [];

    public async Task<IReadOnlyDictionary<string, long>> GetAsync(long parentTableId, AppField key, long tenantId, long appId, CancellationToken ct = default)
    {
        var id = (parentTableId, key.Id);
        Task<IReadOnlyDictionary<string, long>> map;
        lock (_gate)
        {
            if (!_maps.TryGetValue(id, out map!))
                // Shared by every chunk that needs it, so one caller's cancellation must not abandon the read for the others.
                _maps[id] = map = read(parentTableId, key, tenantId, appId, CancellationToken.None);
        }
        try
        {
            return await map.WaitAsync(ct);
        }
        catch
        {
            // A failed read is not kept: the retry (of this chunk or the next) reads the parent again. A caller that only stopped
            // waiting (its own cancellation) leaves the read running for the others.
            if (map.IsCompleted && !map.IsCompletedSuccessfully)
                lock (_gate)
                    if (_maps.TryGetValue(id, out var cached) && ReferenceEquals(cached, map)) _maps.Remove(id);
            throw;
        }
    }
}
