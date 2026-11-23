using System.Collections.Concurrent;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Compute;
using PowerBase.Infrastructure.Compute;

namespace PowerBase.Infrastructure.Persistence;

/// <summary>The pure decisions of read-replica routing, kept apart so they can be unit tested without a database.</summary>
public static class ReadReplicaRouting
{
    /// <summary>meta.Tenant.ReadReplicaMode.</summary>
    public const byte ModeNone = 0, ModeBuiltIn = 1, ModeNamed = 2;

    /// <summary>
    /// The replica connection string for a tenant, or null = use the primary. Mode 1 adds ApplicationIntent=ReadOnly (Premium / Business
    /// Critical / Hyperscale built-in replica); mode 2 points at the named replica database. Anything else, or a switch that is off, = null.
    /// </summary>
    public static string? BuildReadOnlyConnectionString(string primaryConnectionString, bool globalEnabled, bool tenantEnabled, byte mode, string? namedReplicaDatabase)
    {
        if (!globalEnabled || !tenantEnabled) return null;
        var builder = new SqlConnectionStringBuilder(primaryConnectionString);
        switch (mode)
        {
            case ModeBuiltIn:
                builder.ApplicationIntent = ApplicationIntent.ReadOnly;
                return builder.ToString();
            case ModeNamed when !string.IsNullOrWhiteSpace(namedReplicaDatabase):
                builder.InitialCatalog = namedReplicaDatabase;
                builder.ApplicationIntent = ApplicationIntent.ReadOnly;
                return builder.ToString();
            default:
                return null;
        }
    }

    /// <summary>True when the replica is too far behind for the intent (or its lag is unknown): the read goes to the primary.</summary>
    public static bool IsTooFarBehind(double? lagSeconds, ReadReplicaIntent intent, ReadReplicaOptions options)
    {
        if (lagSeconds is null) return true;
        var allowed = intent == ReadReplicaIntent.Background ? options.BackgroundMaxLagSeconds : options.MaxLagSeconds;
        return lagSeconds.Value > allowed;
    }

    /// <summary>207 invalid column, 208 invalid object: the replica has not caught up with a schema change yet. Retry on the primary.</summary>
    public static bool IsReplicaSchemaLag(SqlException ex) => ex.Number is 207 or 208;
}

/// <summary>
/// Measures how far each tenant's replica is behind, from meta.ReplicaHeartbeat (the compute worker writes it on the primary every few
/// seconds; read here through the replica). The result is cached briefly per tenant so the check costs one tiny query every few seconds.
/// </summary>
public interface IReplicaLagMonitor
{
    /// <summary>Seconds the replica is behind; null when it cannot tell (treated as "too far behind").</summary>
    Task<double?> GetLagSecondsAsync(long tenantId, string readOnlyConnectionString, CancellationToken ct = default);
}

public sealed class ReplicaLagMonitor : IReplicaLagMonitor
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ReplicaLagMonitor>? _logger;
    private readonly ConcurrentDictionary<long, (double? Lag, DateTime CheckedUtc)> _cache = new();

    public ReplicaLagMonitor(IConfiguration configuration, ILogger<ReplicaLagMonitor>? logger = null)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<double?> GetLagSecondsAsync(long tenantId, string readOnlyConnectionString, CancellationToken ct = default)
    {
        var options = ComputeConfig.ReadReplica(_configuration);
        if (_cache.TryGetValue(tenantId, out var hit) && DateTime.UtcNow - hit.CheckedUtc < TimeSpan.FromSeconds(Math.Max(1, options.LagCacheSeconds)))
            return hit.Lag;

        double? lag = null;
        try
        {
            await using var conn = new SqlConnection(readOnlyConnectionString);
            await conn.OpenAsync(ct);
            var ms = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT DATEDIFF_BIG(MILLISECOND, BeatOn, SYSUTCDATETIME()) FROM meta.ReplicaHeartbeat WHERE Id = 1", commandTimeout: 5, cancellationToken: ct));
            lag = ms is { } v ? Math.Max(0, v) / 1000d : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Read replica of tenant {TenantId} could not be checked; reads use the primary.", tenantId);
        }

        _cache[tenantId] = (lag, DateTime.UtcNow);
        return lag;
    }
}

/// <summary>Runs a read on a read-only connection and, if the replica does not know a column or table yet, once more on the primary.</summary>
public static class ReadReplicaFallback
{
    public static async Task<T> RunAsync<T>(ITenantConnectionFactory factory, ReadReplicaIntent intent, Func<SqlConnection, Task<T>> read, CancellationToken ct = default)
    {
        try
        {
            await using var replica = await factory.CreateReadOnlyAsync(intent, ct);
            return await read(replica);
        }
        catch (SqlException ex) when (ReadReplicaRouting.IsReplicaSchemaLag(ex))
        {
            await using var primary = await factory.CreateAsync(ct);
            return await read(primary);
        }
    }
}
