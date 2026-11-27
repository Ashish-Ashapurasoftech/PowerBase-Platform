namespace PowerBase.Application.Common.Interfaces;

/// <summary>
/// Process-local cache for rarely-changing tenant metadata (field lists, table/app/report definitions, role permissions).
/// Entries live until the data they mirror is written (the repository that owns the write calls <see cref="Invalidate"/>) or the
/// backstop TTL passes. Every entry belongs to a tenant and a group, e.g. "fields"; a group is invalidated as a whole.
/// </summary>
public interface IMetadataCache
{
    /// <summary>The cached value, or the result of <paramref name="factory"/> (which is then cached). Callers that mutate the value
    /// must copy it first — the same instance is handed to every request. Nothing is cached for a group that is being written.</summary>
    Task<T> GetOrAddAsync<T>(long tenantId, string group, string key, Func<Task<T>> factory) where T : class;

    /// <summary>Drops every entry of the group and keeps it out of the cache for a short while, so a read that overlaps the write
    /// (or a transaction that has not committed yet when <paramref name="inTransaction"/>) cannot store the old data again.
    /// Call it BEFORE the write.</summary>
    void Invalidate(long tenantId, string group, bool inTransaction = false);

    /// <summary>Drops everything cached for the tenant — for changes made outside the application's repositories.</summary>
    void FlushTenant(long tenantId);

    /// <summary>
    /// The shared versions of every group of the tenant and of the global groups (user permissions, sessions), as one string, read for
    /// this request. Any metadata or permission write moves it. Null when the versions cannot be read, or when a group moved less than
    /// the transaction quarantine ago (a write may still be open): a shared result must not be cached or read under it then.
    /// </summary>
    Task<string?> VersionStampAsync(long tenantId) => Task.FromResult<string?>(null);
}
