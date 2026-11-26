namespace PowerBase.Application.Common.Caching;

/// <summary>
/// One HTTP request (or one unit of background work): within it, the metadata cache reads each tenant's shared versions once, at its
/// first cache use — so a permission change committed before the request started is seen by that request, on every instance.
/// Outside a scope (a background loop) versions are re-read at most every <c>MetadataCache:VersionCheckIntervalMs</c>.
/// </summary>
public sealed class CacheVersionScope : IDisposable
{
    private static readonly AsyncLocal<CacheVersionScope?> CurrentScope = new();
    private readonly CacheVersionScope? _parent;
    private readonly HashSet<long> _checkedTenants = [];

    private CacheVersionScope(CacheVersionScope? parent) => _parent = parent;

    public static CacheVersionScope? Current => CurrentScope.Value;

    public static CacheVersionScope Begin()
    {
        var scope = new CacheVersionScope(CurrentScope.Value);
        CurrentScope.Value = scope;
        return scope;
    }

    /// <summary>True the first time a tenant is asked for in this scope (the caller then reads its versions).</summary>
    public bool FirstUse(long tenantId)
    {
        lock (_checkedTenants) return _checkedTenants.Add(tenantId);
    }

    public void Dispose() => CurrentScope.Value = _parent;
}
