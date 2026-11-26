namespace PowerBase.Application.Common.Interfaces;

/// <summary>
/// The authoritative versions of the metadata cache groups, kept in SQL (<c>meta.CacheVersion</c>: in the tenant database for a
/// tenant, in the control database for <see cref="MetadataCacheGroups.GlobalTenant"/>). The cache itself lives in each process's
/// memory; these rows are how a write on one API instance reaches every other instance (ARC-01).
/// </summary>
public interface ICacheVersionStore
{
    /// <summary>Every group's version for the tenant, read from the primary. Null when it cannot be read (no table yet — migration
    /// not applied — or the database is unreachable): the caller must then not trust its cache for security-relevant groups.</summary>
    Task<IReadOnlyDictionary<string, long>?> ReadAsync(long tenantId, CancellationToken ct = default);

    /// <summary>Bumps the group's version (its own statement, committed at once — before the write it announces, so every other
    /// instance stops caching the group for the write's quarantine). Returns false when it could not (no table / unreachable).</summary>
    bool Bump(long tenantId, string group);
}
