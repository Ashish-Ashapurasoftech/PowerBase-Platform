using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Compute;
using PowerBase.Infrastructure.Compute;

namespace PowerBase.ComputeWorker;

/// <summary>
/// Builds stored columns that are switched on but not built yet (first fill of existing records, a rebuild after a version change, a build
/// that stopped half way), one tenant at a time and one field at a time, inside the off-peak window, through the database guard's
/// background slot. Progress is the field's state: Building until the build and its replay finish, then Ready.
/// </summary>
public sealed class BackfillRunner : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ComputeTenantDirectory _tenants;
    private readonly ILogger<BackfillRunner> _logger;

    public BackfillRunner(IServiceProvider services, IConfiguration configuration, ComputeTenantDirectory tenants, ILogger<BackfillRunner> logger)
    {
        _services = services;
        _configuration = configuration;
        _tenants = tenants;
        _logger = logger;
    }

    private readonly Dictionary<long, DateTime> _lastIndexSync = new();

    internal bool IndexSyncDue(long tenantId, bool fieldBecameReady, ComputeOptions options, DateTime utcNow) =>
        fieldBecameReady || !_lastIndexSync.TryGetValue(tenantId, out var last) || utcNow - last >= TimeSpan.FromMinutes(options.IndexSyncMinutes);

    internal void MarkIndexSynced(long tenantId, DateTime utcNow) => _lastIndexSync[tenantId] = utcNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = ComputeConfig.ReadCompute(_configuration);
            if (options.Enabled && ComputeConfig.IsOffPeak(options, DateTime.UtcNow))
            {
                try
                {
                    foreach (var tenantId in await _tenants.ListComputeTenantsAsync(stoppingToken))
                    {
                        try
                        {
                            using var scope = _services.CreateScope();
                            scope.ServiceProvider.GetRequiredService<IQueryContext>().SetTenantId(tenantId);
                            var builder = scope.ServiceProvider.GetRequiredService<MaterializeBuilder>();
                            // Decision Q3: every calculated field not decided yet (new fields too) is switched on, then built below.
                            if (options.StoreAllCalculatedFields && await builder.SwitchOnUndecidedAsync(stoppingToken) is > 0 and var switchedOn)
                                _logger.LogInformation("Compute: tenant {TenantId}: {Count} calculated field(s) switched on (store every Lookup/Summary/Formula).", tenantId, switchedOn);
                            var outcomes = await builder.BuildPendingAsync(options, stoppingToken);
                            foreach (var o in outcomes.Where(o => o.Outcome != "later"))
                                _logger.LogInformation("Compute backfill: tenant {TenantId} field {FieldId} (t{TableId}.c_{Fid}): {Outcome}, {Rows} row(s), {Seconds:0.0}s. {Reason}",
                                    tenantId, o.FieldId, o.TableId, o.Fid, o.Outcome, o.Rows, o.Took.TotalSeconds, o.Reason);

                            // Indexes of stored columns: right after a field became Ready, otherwise every IndexSyncMinutes (reports change).
                            if (options.IndexesEnabled && IndexSyncDue(tenantId, outcomes.Any(o => o.Outcome == "ready"), options, DateTime.UtcNow))
                            {
                                var indexes = await scope.ServiceProvider.GetRequiredService<MaterializedIndexMaintainer>().SyncAsync(options, stoppingToken);
                                if (!indexes.Any(i => i.Outcome == "busy")) MarkIndexSynced(tenantId, DateTime.UtcNow);
                            }
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                        catch (Exception ex) { _logger.LogError(ex, "Compute backfill: tenant {TenantId} failed this round.", tenantId); }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { _logger.LogError(ex, "Compute backfill round failed."); }
            }

            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(10, options.BackfillPollSeconds)), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
