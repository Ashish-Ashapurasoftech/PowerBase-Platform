using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;

namespace PowerBase.Infrastructure.Persistence;

public class TenantConnectionResolver : ITenantConnectionResolver
{
    private readonly IControlConnectionFactory _controlFactory;
    private readonly ISecretResolver _secretResolver;
    private readonly IMemoryCache _cache;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private const string GetRoutingSql = """
        SELECT DatabaseName, ServerRef, ConnectionSecretRef, ProvisioningState
        FROM meta.Tenant
        WHERE Id = @tenantId AND IsDeleted = 0
        """;

    public TenantConnectionResolver(
        IControlConnectionFactory controlFactory,
        ISecretResolver secretResolver,
        IMemoryCache cache)
    {
        _controlFactory = controlFactory;
        _secretResolver = secretResolver;
        _cache = cache;
    }

    public async Task<string> ResolveAsync(long tenantId, CancellationToken ct = default)
    {
        var cacheKey = $"tenant_conn_{tenantId}";
        if (_cache.TryGetValue(cacheKey, out string? cached))
            return cached!;

        await using var conn = _controlFactory.Create();
        await conn.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<TenantRoutingRow>(
            new CommandDefinition(GetRoutingSql, new { tenantId }, cancellationToken: ct));

        if (row is null)
            throw new InvalidOperationException($"Tenant {tenantId} not found.");
        if (row.ProvisioningState != "Ready")
            throw new InvalidOperationException($"Tenant {tenantId} is not ready (state: {row.ProvisioningState}).");

        var baseConnStr = await _secretResolver.ResolveAsync(row.ServerRef, row.ConnectionSecretRef, ct);
        var builder = new SqlConnectionStringBuilder(baseConnStr)
        {
            InitialCatalog = row.DatabaseName
        };
        var result = builder.ToString();

        _cache.Set(cacheKey, result, CacheTtl);
        return result;
    }

    public void Invalidate(long tenantId)
        => _cache.Remove($"tenant_conn_{tenantId}");

    private sealed record TenantRoutingRow(
        string DatabaseName,
        string? ServerRef,
        string? ConnectionSecretRef,
        string ProvisioningState);
}
