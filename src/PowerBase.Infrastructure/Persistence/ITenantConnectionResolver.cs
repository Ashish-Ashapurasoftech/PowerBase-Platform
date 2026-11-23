namespace PowerBase.Infrastructure.Persistence;

public interface ITenantConnectionResolver
{
    Task<string> ResolveAsync(long tenantId, CancellationToken ct = default);

    /// <summary>
    /// The tenant's read-replica connection string, or null when reads must use the primary: the feature is off (globally or for the
    /// tenant), the tenant has no replica (ReadReplicaMode = 0), or the control migration that adds the columns has not run yet.
    /// </summary>
    Task<string?> ResolveReadOnlyAsync(long tenantId, CancellationToken ct = default) => Task.FromResult<string?>(null);

    void Invalidate(long tenantId);
}
