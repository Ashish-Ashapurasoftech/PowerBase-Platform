using System.Collections.Concurrent;
using PowerBase.Application.ConnectedTables.Interfaces;

namespace PowerBase.Application.ConnectedTables;

/// <summary>Which tables of a tenant are connected, cached briefly so the read-only write filter costs (almost) nothing on
/// ordinary tables. The module invalidates a tenant's entry whenever it creates or removes a connected table; the short
/// lifetime covers other API instances.</summary>
public static class ConnectedTableCache
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<long, Entry> Entries = new();

    public sealed record Entry(DateTime LoadedOn, IReadOnlySet<long> Ids, IReadOnlySet<Guid> PublicIds);

    public static async Task<Entry> GetAsync(long tenantId, IConnectedTableRepository repository, CancellationToken ct)
    {
        if (Entries.TryGetValue(tenantId, out var cached) && DateTime.UtcNow - cached.LoadedOn < Lifetime) return cached;
        var tables = await repository.ListConnectedAppTablesAsync(ct);
        var entry = new Entry(DateTime.UtcNow, tables.Select(t => t.Id).ToHashSet(), tables.Select(t => t.PublicId).ToHashSet());
        Entries[tenantId] = entry;
        return entry;
    }

    public static void Invalidate(long tenantId) => Entries.TryRemove(tenantId, out _);
}
