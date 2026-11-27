namespace PowerBase.Infrastructure.Repositories;

/// <summary>An id held by the metadata cache, which stores reference types.</summary>
internal sealed record CachedId(long Value);

/// <summary>A value (possibly null, e.g. "no role") held by the metadata cache, which stores non-null reference types. Round-trips
/// through the Redis L2 as JSON.</summary>
internal sealed record CachedValue<T>(T Value);
