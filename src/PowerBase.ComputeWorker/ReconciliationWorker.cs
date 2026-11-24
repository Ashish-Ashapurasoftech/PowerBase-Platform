using PowerBase.Application.Common.Interfaces;
using PowerBase.Infrastructure.Compute;

namespace PowerBase.ComputeWorker;

/// <summary>
/// Once a day (Compute:ReconcileHourUtc, inside the off-peak window) compares every Ready stored column with a fresh computation and fixes
/// what differs. The Phase 3 gate is "0 differences for 7 days": the per-field log lines and the daily total are that evidence.
/// </summary>
public sealed class ReconciliationWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ComputeTenantDirectory _tenants;
    private readonly ILogger<ReconciliationWorker> _logger;
    private DateOnly? _lastRunDay;

    public ReconciliationWorker(IServiceProvider services, IConfiguration configuration, ComputeTenantDirectory tenants, ILogger<ReconciliationWorker> logger)
    {
        _services = services;
        _configuration = configuration;
        _tenants = tenants;
        _logger = logger;
    }

    /// <summary>Due once per UTC day, at or after the configured hour.</summary>
    internal static bool IsDue(DateTime utcNow, int reconcileHourUtc, DateOnly? lastRunDay) =>
        utcNow.Hour >= reconcileHourUtc && lastRunDay != DateOnly.FromDateTime(utcNow);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = ComputeConfig.ReadCompute(_configuration);
            var now = DateTime.UtcNow;
            if (options.Enabled && IsDue(now, options.ReconcileHourUtc, _lastRunDay) && ComputeConfig.IsOffPeak(options, now))
            {
                _lastRunDay = DateOnly.FromDateTime(now);
                long fields = 0, differences = 0;
                try
                {
                    foreach (var tenantId in await _tenants.ListComputeTenantsAsync(stoppingToken))
                    {
                        try
                        {
                            using var scope = _services.CreateScope();
                            scope.ServiceProvider.GetRequiredService<IQueryContext>().SetTenantId(tenantId);
                            var builder = scope.ServiceProvider.GetRequiredService<MaterializeBuilder>();
                            var outcomes = await builder.ReconcileAsync(options, stoppingToken);
                            // Decision Q3: fields refused earlier get another look once a day (their inputs may allow them now).
                            if (options.StoreAllCalculatedFields && await builder.RecheckNotAllowedAsync(stoppingToken, options.MinRecordsForStoredColumns) is > 0 and var rechecked)
                                _logger.LogInformation("Compute: tenant {TenantId}: {Count} field(s) the rules refused before can be stored now; switched on.", tenantId, rechecked);
                            fields += outcomes.Count(o => o.Outcome == "checked");
                            differences += outcomes.Sum(o => o.Differences);
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                        catch (Exception ex) { _logger.LogError(ex, "Compute reconciliation: tenant {TenantId} failed.", tenantId); }
                    }
                    _logger.LogInformation("Compute reconciliation finished: {Fields} stored field(s) checked, {Differences} difference(s) found and fixed.", fields, differences);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { _logger.LogError(ex, "Compute reconciliation round failed."); }
            }

            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
