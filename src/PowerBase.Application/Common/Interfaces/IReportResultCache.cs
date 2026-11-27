namespace PowerBase.Application.Common.Interfaces;

/// <summary>
/// Doc 23 §5 / doc 34: shared cache (Redis) of finished Summary / Chart report answers of unencrypted apps, TTL ≤ 60 s. The key holds
/// the tenant, the user, every metadata / permission version (<see cref="IMetadataCache.VersionStampAsync"/>), the tenant database's
/// rowversion counter (any data write in that database moves it) and a hash of the whole question. An answer is stored only when no
/// write was open as it was read. Off unless ResultCache:Enabled and Redis is configured, and always off with read replicas on.
/// </summary>
public interface IReportResultCache
{
    /// <summary>Looks the question up. <see cref="ReportResultLookup.Key"/> is null when this answer may not be cached now (off, Redis
    /// down, versions unknown, a write open, a report known not to be cacheable).</summary>
    Task<ReportResultLookup> LookupAsync(Guid reportPublicId, string question, CancellationToken ct = default);

    /// <summary>Stores the finished response body under the lookup's key (not awaited by the request; failures only cost a later miss).</summary>
    void Store(ReportResultLookup lookup, byte[] body);

    /// <summary>This report's answers are not cacheable (a Table report, an encrypted app): skip the lookups for a while.</summary>
    void MarkNotCacheable(Guid reportPublicId);
}

/// <summary>A lookup: the key to store under (null = do not store), and the cached body on a hit.</summary>
public sealed record ReportResultLookup(string? Key, byte[]? Body)
{
    public static readonly ReportResultLookup None = new(null, null);
}
