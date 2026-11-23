using PowerBase.Application.Common.Interfaces;
using PowerBase.Infrastructure.Compute;

namespace PowerBase.ComputeWorker;

/// <summary>
/// Stored fields whose value moves with the date (Today() formulas, relative-date summary criteria) are worked out again after midnight UTC
/// — the date the read path uses — for every tenant with compute on. Checked every few minutes, apart from the long first builds, so a new
/// day's values never wait behind them. A field already made current today is skipped (MaterializedOn), so restarts repeat nothing.
/// </summary>
public sealed class DailyRefreshRunner : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ComputeTenantDirectory _tenants;
    private readonly ILogger<DailyRefreshRunner> _logger;

    public DailyRefreshRunner(IServiceProvider services, IConfiguration configuration, ComputeTenantDirectory tenants, ILogger<DailyRefreshRunner> logger)
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
            var options = ComputeConfig.ReadCompute(_configuration);
            if (options.Enabled)
            {
                try
                {
                    foreach (var tenantId in await _tenants.ListComputeTenantsAsync(stoppingToken))
                    {
                        try
                        {
                            using var scope = _services.CreateScope();
                            scope.ServiceProvider.GetRequiredService<IQueryContext>().SetTenantId(tenantId);
                            await scope.ServiceProvider.GetRequiredService<MaterializeBuilder>().RefreshDailyAsync(options, stoppingToken);
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                        catch (Exception ex) { _logger.LogError(ex, "Compute daily refresh: tenant {TenantId} failed this round.", tenantId); }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { _logger.LogError(ex, "Compute daily refresh round failed."); }
            }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
