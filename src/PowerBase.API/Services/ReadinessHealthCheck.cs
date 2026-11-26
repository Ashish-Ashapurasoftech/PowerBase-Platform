using Dapper;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.API.Services;

/// <summary>
/// Readiness for the load balancer (App Service health check, Front Door probe): Healthy only once the host has fully started and the
/// control database answers — a new instance gets traffic when it can serve it, and an instance that lost its database is taken out
/// of rotation. Cheap (one SELECT 1, 3 s timeout); no tenant database is touched.
/// </summary>
public sealed class ReadinessHealthCheck(IControlConnectionFactory control, IHostApplicationLifetime lifetime) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        if (!lifetime.ApplicationStarted.IsCancellationRequested)
            return HealthCheckResult.Unhealthy("starting");
        if (!StartupWarmup.Completed)
            return HealthCheckResult.Unhealthy("warming up");   // pools, DI graph and JIT first (StartupWarmup, at most 30 s)
        if (lifetime.ApplicationStopping.IsCancellationRequested)
            return HealthCheckResult.Unhealthy("stopping");   // drain: the load balancer stops sending new requests
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await using var connection = control.Create();
            await connection.OpenAsync(timeout.Token);
            await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: timeout.Token));
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("control database unreachable", ex);
        }
    }
}
