using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Infrastructure.Compute;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.ComputeWorker;

/// <summary>
/// Writes meta.ReplicaHeartbeat on the PRIMARY of every tenant whose read replica is switched on, every few seconds. Readers compare it
/// on the replica (ReplicaLagMonitor) to decide whether the replica is close enough behind. Does nothing while ReadReplica:Enabled is off.
/// </summary>
public sealed class ReplicaHeartbeatWriter : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ComputeTenantDirectory _tenants;
    private readonly ILogger<ReplicaHeartbeatWriter> _logger;

    public ReplicaHeartbeatWriter(IServiceProvider services, IConfiguration configuration, ComputeTenantDirectory tenants, ILogger<ReplicaHeartbeatWriter> logger)
    {
        _services = services;
        _configuration = configuration;
        _tenants = tenants;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = ComputeConfig.ReadReplica(_configuration);
            if (options.Enabled)
            {
                try
                {
                    foreach (var tenantId in await _tenants.ListReplicaTenantsAsync(stoppingToken))
                    {
                        try
                        {
                            using var scope = _services.CreateScope();
                            scope.ServiceProvider.GetRequiredService<IQueryContext>().SetTenantId(tenantId);
                            await using var conn = await scope.ServiceProvider.GetRequiredService<ITenantConnectionFactory>().CreateAsync(stoppingToken);
                            await conn.OpenAsync(stoppingToken);
                            await conn.ExecuteAsync(new CommandDefinition("""
                                IF OBJECT_ID('meta.ReplicaHeartbeat') IS NOT NULL
                                    UPDATE meta.ReplicaHeartbeat SET BeatOn = SYSUTCDATETIME() WHERE Id = 1
                                """, commandTimeout: 5, cancellationToken: stoppingToken));
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // A missed beat only makes readers use the primary: the safe direction.
                            _logger.LogWarning(ex, "Replica heartbeat: tenant {TenantId} could not be written.", tenantId);
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { _logger.LogError(ex, "Replica heartbeat cycle failed."); }
            }

            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.Enabled ? options.HeartbeatIntervalSeconds : 30)), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
