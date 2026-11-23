using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Compute;
using PowerBase.Infrastructure.Compute;

namespace PowerBase.ComputeWorker;

/// <summary>
/// Makes sure no compute change is lost on its way to the worker (copy of the search SearchOutboxRelay, in the worker's own host).
/// For every tenant with compute switched on:
/// - with Compute:RepublishEnabled: re-publishes rows still waiting after the grace period (the message was lost), one message per table;
/// - otherwise (Phase 2 "observe" mode, nothing consumes the rows yet): deletes rows older than Compute:ObserveRetentionDays;
/// - every few minutes logs what is waiting (the Phase 2 gate: outbox rows match writes).
/// </summary>
public sealed class ComputeOutboxRelay : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ComputeTenantDirectory _tenants;
    private readonly IComputeQueuePublisher _publisher;
    private readonly ILogger<ComputeOutboxRelay> _logger;
    private DateTime _lastStatsUtc = DateTime.MinValue;
    private DateTime _lastPurgeUtc = DateTime.MinValue;

    public ComputeOutboxRelay(IServiceProvider services, IConfiguration configuration, ComputeTenantDirectory tenants,
        IComputeQueuePublisher publisher, ILogger<ComputeOutboxRelay> logger)
    {
        _services = services;
        _configuration = configuration;
        _tenants = tenants;
        _publisher = publisher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = ComputeConfig.ReadCompute(_configuration);
            try
            {
                if (options.Enabled)
                {
                    var tenants = await _tenants.ListComputeTenantsAsync(stoppingToken);
                    var measure = DateTime.UtcNow - _lastStatsUtc > TimeSpan.FromMinutes(5);
                    var purge = !options.RepublishEnabled && DateTime.UtcNow - _lastPurgeUtc > TimeSpan.FromHours(1);

                    await Parallel.ForEachAsync(tenants,
                        new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.TenantConcurrency), CancellationToken = stoppingToken },
                        async (tenantId, token) =>
                        {
                            try { await RunTenantAsync(tenantId, options, measure, purge, token); }
                            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                            catch (Exception ex) { _logger.LogError(ex, "Compute outbox: tenant {TenantId} could not be processed.", tenantId); }
                        });

                    if (measure) _lastStatsUtc = DateTime.UtcNow;
                    if (purge) _lastPurgeUtc = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Compute outbox relay cycle failed; trying again after the pause.");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, options.RelayIntervalSeconds)), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunTenantAsync(long tenantId, ComputeOptions options, bool measure, bool purge, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IQueryContext>().SetTenantId(tenantId);
        var outbox = scope.ServiceProvider.GetRequiredService<IComputeOutbox>();

        if (options.RepublishEnabled)
        {
            var due = await outbox.ClaimDueAsync(options.BatchSize, TimeSpan.FromSeconds(options.GraceSeconds),
                TimeSpan.FromSeconds(options.RetryAfterSeconds), options.MaxAttempts, ct);
            if (due.Count > 0)
            {
                var messages = ComputeOutboxPlanner.ToMessages(tenantId, due);
                try
                {
                    await _publisher.PublishBatchAsync(messages, ct);
                    _logger.LogWarning("Compute outbox: re-published {Tables} table(s) ({Rows} waiting row(s)) for tenant {TenantId}.", messages.Count, due.Count, tenantId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The attempt is counted, so the same rows are tried again only after RetryAfterSeconds.
                    await outbox.RecordFailureAsync(due.Select(d => d.Id).ToList(), ex.Message, ct);
                }
            }
        }

        if (purge)
        {
            var removed = await outbox.PurgeOlderThanAsync(TimeSpan.FromDays(Math.Max(1, options.ObserveRetentionDays)), ct);
            if (removed > 0)
                _logger.LogInformation("Compute outbox (observe mode): removed {Count} row(s) older than {Days} day(s) for tenant {TenantId}.", removed, options.ObserveRetentionDays, tenantId);
        }

        if (measure)
        {
            var byTable = await outbox.CountPendingByTableAsync(ct);
            var stats = await outbox.GetStatsAsync(options.MaxAttempts, ct);
            _logger.LogInformation("Compute outbox: tenant {TenantId} has {Pending} waiting row(s) in {Tables} table(s), {Parked} parked, oldest {Oldest:u}.",
                tenantId, stats.Pending, byTable.Count, stats.Parked, stats.OldestPendingUtc);
        }
    }
}
