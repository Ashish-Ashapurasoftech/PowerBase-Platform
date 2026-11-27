using PowerBase.Application.ConnectedTables.Interfaces;

namespace PowerBase.Application.ConnectedTables;

/// <summary>Which tables of a tenant are connected, for the read-only write filter. The list itself is held by the shared metadata
/// cache (IConnectedTableRepository.ListConnectedAppTablesAsync, ConnectedTables group, Redis L2 when configured): creating or removing
/// a connected table drops it on every API instance, so there is no per-instance copy here any more (it used to live 30 s).</summary>
public static class ConnectedTableCache
{
    public sealed record Entry(IReadOnlySet<long> Ids, IReadOnlySet<Guid> PublicIds);

    public static async Task<Entry> GetAsync(long tenantId, IConnectedTableRepository repository, CancellationToken ct)
    {
        var tables = await repository.ListConnectedAppTablesAsync(ct);
        return new Entry(tables.Select(t => t.Id).ToHashSet(), tables.Select(t => t.PublicId).ToHashSet());
    }
}
