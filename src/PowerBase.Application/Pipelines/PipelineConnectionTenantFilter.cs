using PowerBase.Application.Tenants;

namespace PowerBase.Application.Pipelines;

// powerflowextraRd: the PowerFlows Connection picker shows only the logged-in tenant (plus saved accounts, which the UI
// adds from /pipeline-connections) — not every tenant the user can reach. Tenants a saved pipeline already references
// can be asked for via `include`, so existing pipelines keep their connection label; they must still be tenants the
// user has access to.
public static class PipelineConnectionTenantFilter
{
    public static IReadOnlyList<TenantItem> Filter(
        IReadOnlyList<TenantItem> accessibleTenants,
        Guid? currentTenantPublicId,
        IEnumerable<Guid>? includePublicIds = null)
    {
        var keep = new HashSet<Guid>(includePublicIds ?? Array.Empty<Guid>());
        if (currentTenantPublicId.HasValue) keep.Add(currentTenantPublicId.Value);
        return accessibleTenants
            .Where(t => keep.Contains(t.PublicId))
            // current tenant first so it is the default/top option
            .OrderBy(t => t.PublicId == currentTenantPublicId ? 0 : 1)
            .ToList();
    }
}
