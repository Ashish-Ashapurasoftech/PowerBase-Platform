using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace PowerBase.Infrastructure.Persistence;

public class TenantConnectionResolver : ITenantConnectionResolver
{
    private readonly IControlConnectionFactory _controlFactory;
    private readonly ISecretResolver _secretResolver;
    private readonly IMemoryCache _cache;
    private readonly IConfiguration? _configuration;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private const string GetRoutingSql = """
        SELECT DatabaseName, ServerRef, ConnectionSecretRef, ProvisioningState, SupportsSecureEnclaves
        FROM meta.Tenant
        WHERE Id = @tenantId AND IsDeleted = 0
        """;

    public TenantConnectionResolver(
        IControlConnectionFactory controlFactory,
        ISecretResolver secretResolver,
        IMemoryCache cache,
        IConfiguration? configuration = null)
    {
        _configuration = configuration;
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

        if (row.SupportsSecureEnclaves)
        {
            builder.ColumnEncryptionSetting = SqlConnectionColumnEncryptionSetting.Enabled;
        }

        var result = builder.ToString();

        _cache.Set(cacheKey, result, CacheTtl);
        return result;
    }

    private const string GetReplicaRoutingSql = """
        SELECT ReadReplicaEnabled, ReadReplicaMode, ReadReplicaDatabaseName
        FROM meta.Tenant
        WHERE Id = @tenantId AND IsDeleted = 0
        """;

    public async Task<string?> ResolveReadOnlyAsync(long tenantId, CancellationToken ct = default)
    {
        // Global switch first: while it is off nothing is read from the control database and every read stays on the primary.
        if (_configuration is null || !Compute.ComputeConfig.ReadReplica(_configuration).Enabled) return null;

        var cacheKey = $"tenant_conn_ro_{tenantId}";
        if (_cache.TryGetValue(cacheKey, out string? cached))
            return cached == string.Empty ? null : cached;

        string? result = null;
        try
        {
            await using var conn = _controlFactory.Create();
            await conn.OpenAsync(ct);
            var row = await conn.QuerySingleOrDefaultAsync<ReplicaRoutingRow>(
                new CommandDefinition(GetReplicaRoutingSql, new { tenantId }, cancellationToken: ct));
            if (row is not null)
            {
                var primary = await ResolveAsync(tenantId, ct);
                result = ReadReplicaRouting.BuildReadOnlyConnectionString(primary, globalEnabled: true, row.ReadReplicaEnabled, row.ReadReplicaMode, row.ReadReplicaDatabaseName);
            }
        }
        catch (SqlException ex) when (ex.Number == 207)
        {
            result = null; // control migration 018 not applied yet: no replica columns, so no replica
        }

        _cache.Set(cacheKey, result ?? string.Empty, CacheTtl);
        return result;
    }

    public void Invalidate(long tenantId)
    {
        _cache.Remove($"tenant_conn_{tenantId}");
        _cache.Remove($"tenant_conn_ro_{tenantId}");
    }

    private sealed record ReplicaRoutingRow(bool ReadReplicaEnabled, byte ReadReplicaMode, string? ReadReplicaDatabaseName);

    private sealed record TenantRoutingRow(
        string DatabaseName,
        string? ServerRef,
        string? ConnectionSecretRef,
        string ProvisioningState,
        bool SupportsSecureEnclaves);
}
