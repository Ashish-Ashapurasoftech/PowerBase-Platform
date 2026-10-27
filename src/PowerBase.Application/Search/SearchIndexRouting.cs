using System;
using System.Collections.Generic;
using System.Linq;

namespace PowerBase.Application.Search;

/// <summary>
/// Which Azure AI Search index a tenant's documents live in, decided only from configuration and the tenant id.
/// It used to depend on what each process had happened to see in memory (a list of known shards, a "currently active" shard), so a
/// restart or a second instance disagreed about where a record was: records disappeared from search and updates left stale copies.
/// Now every instance, and every restart, computes the same answer, and ALL of a tenant's documents are in ONE index, so no query
/// ever has to page across indexes.
/// </summary>
public static class SearchIndexRouting
{
    /// <summary>
    /// - a tenant listed as dedicated (and per-tenant indexes switched on) has its own index "pb-tenant-{id}";
    /// - otherwise, with <paramref name="shardCount"/> above 1, the tenant belongs to shard "pb-shard-NNN" chosen by tenant id;
    /// - otherwise everything is in <paramref name="defaultIndex"/>.
    /// The shard count is a deployment decision: changing it moves tenants between indexes and needs a full reindex.
    /// </summary>
    public static string TenantIndex(long tenantId, string defaultIndex, int shardCount, bool perTenantIndexes, IReadOnlySet<long> dedicatedTenants)
    {
        if (perTenantIndexes && tenantId > 0 && dedicatedTenants.Contains(tenantId))
            return $"pb-tenant-{tenantId}";
        if (shardCount > 1)
            return $"pb-shard-{(Math.Abs(tenantId) % shardCount) + 1:D3}";
        return defaultIndex;
    }

    public static bool IsWideIndexName(string indexName) =>
        indexName.StartsWith("pb-tbl-", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A table's own index counts against the search service's index limit (15 on Basic, 50 on S1, shared with every other index). It may
    /// be used if it already exists or if fewer than <paramref name="budget"/> such indexes exist; otherwise the table stays in the shared
    /// index and its filters on fields without a slot run in SQL.
    /// </summary>
    public static bool WithinWideIndexBudget(IEnumerable<string> existingIndexNames, string wideIndexName, int budget)
    {
        var names = existingIndexNames.ToList();
        return names.Contains(wideIndexName, StringComparer.OrdinalIgnoreCase)
               || names.Count(IsWideIndexName) < budget;
    }
}

/// <summary>Guards for the admin endpoints that drop the live index.</summary>
public static class SearchIndexSafety
{
    /// <summary>
    /// Dropping an index is refused unless the deployment allows it (<c>AzureAiSearch:AllowDestructiveAdmin</c>, off by default) AND the caller
    /// repeats the exact index name in <c>confirm</c>. Returns the reason to refuse, or null when it may proceed.
    /// </summary>
    public static string? RefuseDestructive(string targetIndex, string? confirm, bool allowedByConfiguration)
    {
        if (!allowedByConfiguration)
            return "Dropping a search index is disabled. Set AzureAiSearch:AllowDestructiveAdmin to true for the time it is needed.";
        if (!string.Equals(confirm, targetIndex, StringComparison.Ordinal))
            return $"This drops index '{targetIndex}' and everything in it. Repeat the index name in the 'confirm' parameter to proceed.";
        return null;
    }
}
