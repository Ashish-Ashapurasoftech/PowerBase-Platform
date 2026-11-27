using System.Text.Json;

namespace PowerBase.Infrastructure.Repositories;

/// <summary>
/// Deep copies of metadata-cache values for entities without a Clone() (form layouts, rules, pages): the cached instance is shared by
/// every request, so a caller must never get it. A JSON round trip with the options of the Redis L2 (MetadataCache), so a type that
/// copies correctly here also survives L2 — the unit tests check both for every type cached this way.
/// </summary>
internal static class CacheCopy
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General);

    public static T Deep<T>(T value) where T : class =>
        JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, Options), Options)
        ?? throw new InvalidOperationException($"A cached {typeof(T).Name} could not be copied.");
}
