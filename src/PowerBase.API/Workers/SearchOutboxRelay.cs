using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Search;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Services;

namespace PowerBase.API.Workers;

/// <summary>
/// Makes sure no record change is lost on its way to the search index.
/// Every record write also writes a row to the tenant's meta.SearchOutbox (in the same transaction). The indexer deletes the row once the
/// record is in the index. This service:
/// - every few seconds re-publishes rows that are still there after a grace period (the message was lost, or the indexer failed);
/// - every few minutes measures what is pending, for the health endpoint;
/// - once a day checks every tenant, logs what is still unconfirmed, and re-drives rows that ran out of attempts.
/// </summary>
public class SearchOutboxRelay : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IControlConnectionFactory _control;
    private readonly ILogger<SearchOutboxRelay> _logger;
    private readonly SearchOutboxOptions _options;
    private readonly SearchPipelineState? _state;
    private DateTime _nextSweepUtc;
    private DateTime _lastStatsUtc = DateTime.MinValue;

    public SearchOutboxRelay(IServiceProvider serviceProvider, IControlConnectionFactory control, IConfiguration configuration, ILogger<SearchOutboxRelay> logger)
    {
        _serviceProvider = serviceProvider;
        _control = control;
        _logger = logger;
        _state = serviceProvider.GetService<SearchPipelineState>();
        _options = configuration.GetSection("Search:Outbox").Get<SearchOutboxOptions>() ?? new SearchOutboxOptions();
        _nextSweepUtc = SearchOutboxPlanner.NextSweepUtc(DateTime.UtcNow, _options.DailySweepHourUtc);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Search outbox relay is switched off (Search:Outbox:Enabled=false).");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(5, _options.RelayIntervalSeconds));
        _logger.LogInformation("Search outbox relay started: every {Interval}, grace {Grace}s, {Attempts} attempts per change.", interval, _options.GraceSeconds, _options.MaxAttempts);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var tenants = await ListTenantsAsync(stoppingToken);
                await ForEachTenantAsync(tenants, RelayTenantAsync, stoppingToken);
                _state?.MarkRelayCycle();

                if (DateTime.UtcNow >= _nextSweepUtc)
                {
                    await RunSweepAsync(tenants, stoppingToken);
                    _nextSweepUtc = SearchOutboxPlanner.NextSweepUtc(DateTime.UtcNow, _options.DailySweepHourUtc);
                }
                else if (DateTime.UtcNow - _lastStatsUtc > TimeSpan.FromMinutes(5))
                {
                    await MeasureAsync(tenants, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Search outbox relay cycle failed; trying again after the pause.");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<List<long>> ListTenantsAsync(CancellationToken ct)
    {
        await using var conn = _control.Create();
        await conn.OpenAsync(ct);
        var ids = await conn.QueryAsync<long>(new CommandDefinition(
            "SELECT Id FROM meta.Tenant WHERE IsDeleted = 0 AND ProvisioningState = 'Ready'", cancellationToken: ct));
        return ids.ToList();
    }

    private async Task ForEachTenantAsync(IEnumerable<long> tenants, Func<long, CancellationToken, Task> action, CancellationToken ct)
    {
        await Parallel.ForEachAsync(tenants, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _options.TenantConcurrency), CancellationToken = ct },
            async (tenantId, token) =>
            {
                try { await action(tenantId, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { _logger.LogError(ex, "Search outbox: tenant {TenantId} could not be processed.", tenantId); }
            });
    }

    private async Task RelayTenantAsync(long tenantId, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IQueryContext>().SetTenantId(tenantId);
        var outbox = scope.ServiceProvider.GetRequiredService<ISearchOutbox>();

        await RelayFieldJobsAsync(scope.ServiceProvider, tenantId, ct);

        var due = await outbox.ClaimDueAsync(
            _options.BatchSize, TimeSpan.FromSeconds(_options.GraceSeconds), TimeSpan.FromSeconds(_options.RetryAfterSeconds), _options.MaxAttempts, ct);
        if (due.Count == 0) return;

        var messages = SearchOutboxPlanner.ToMessages(tenantId, due);
        try
        {
            await scope.ServiceProvider.GetRequiredService<IMessagePublisher>().PublishBatchAsync(messages, ct);
            _logger.LogWarning("Search outbox: re-published {Count} unconfirmed index change(s) for tenant {TenantId}.", messages.Count, tenantId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The attempt is already counted, so the same rows are not hammered; they are tried again after RetryAfterSeconds.
            _logger.LogError(ex, "Search outbox: could not re-publish {Count} change(s) for tenant {TenantId}.", messages.Count, tenantId);
            await outbox.RecordFailureAsync(due.Select(d => d.Id).ToList(), ex.Message, ct);
        }
    }

    /// <summary>Re-sends field jobs whose progress stopped (a page was lost or dead-lettered), from where they stopped.</summary>
    private async Task RelayFieldJobsAsync(IServiceProvider services, long tenantId, CancellationToken ct)
    {
        var jobs = services.GetService<ISearchFieldJobs>();
        if (jobs == null) return;

        var stalled = await jobs.ClaimStalledAsync(
            _options.BatchSize, TimeSpan.FromMinutes(Math.Max(1, _options.FieldJobStalledMinutes)), _options.MaxAttempts, ct);
        if (stalled.Count == 0) return;

        var messages = stalled.Select(j => FieldSlotSync.Message(tenantId, j.AppId, j.TableId, j.FieldId, j.Action == IndexAction.NullifyField,
            j.Slot != null ? new SlotRef(j.Slot, string.Empty, j.EndSlot) : null, j.AfterId, j.Id)).ToList();
        try
        {
            await services.GetRequiredService<IMessagePublisher>().PublishBatchAsync(messages, ct);
            _logger.LogWarning("Search field jobs: re-sent {Count} stalled job(s) of tenant {TenantId} from where they stopped.", messages.Count, tenantId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The attempt is counted and the jobs are taken again once they look stalled again.
            _logger.LogError(ex, "Search field jobs: could not re-send {Count} stalled job(s) of tenant {TenantId}.", messages.Count, tenantId);
        }
    }

    private async Task MeasureAsync(IReadOnlyList<long> tenants, CancellationToken ct)
    {
        _lastStatsUtc = DateTime.UtcNow;
        long pending = 0, parked = 0;
        DateTime? oldest = null;
        await ForEachTenantAsync(tenants, async (tenantId, token) =>
        {
            using var scope = _serviceProvider.CreateScope();
            scope.ServiceProvider.GetRequiredService<IQueryContext>().SetTenantId(tenantId);
            var stats = await scope.ServiceProvider.GetRequiredService<ISearchOutbox>().GetStatsAsync(_options.MaxAttempts, token);
            Interlocked.Add(ref pending, stats.Pending);
            Interlocked.Add(ref parked, stats.Parked);
            if (stats.OldestPendingUtc is { } o)
                lock (this) { if (oldest == null || o < oldest) oldest = o; }
        }, ct);

        _state?.SetOutbox(pending, parked, oldest);
        if (oldest is { } age && DateTime.UtcNow - age > TimeSpan.FromMinutes(_options.StaleAfterMinutes))
            _logger.LogError("Search outbox: {Pending} change(s) are waiting, the oldest since {Oldest:u}. The indexer or the queue may be stuck.", pending, age);
    }

    /// <summary>The once-a-day check: what is still unconfirmed in each tenant, and a fresh start for rows that gave up.</summary>
    private async Task RunSweepAsync(IReadOnlyList<long> tenants, CancellationToken ct)
    {
        _logger.LogInformation("Search outbox daily check started for {Count} tenant(s).", tenants.Count);
        long totalPending = 0, totalParked = 0, released = 0;
        DateTime? oldest = null;

        await ForEachTenantAsync(tenants, async (tenantId, token) =>
        {
            using var scope = _serviceProvider.CreateScope();
            scope.ServiceProvider.GetRequiredService<IQueryContext>().SetTenantId(tenantId);
            var outbox = scope.ServiceProvider.GetRequiredService<ISearchOutbox>();

            var stats = await outbox.GetStatsAsync(_options.MaxAttempts, token);
            Interlocked.Add(ref totalPending, stats.Pending);
            Interlocked.Add(ref totalParked, stats.Parked);
            if (stats.OldestPendingUtc is { } o) lock (this) { if (oldest == null || o < oldest) oldest = o; }

            if (stats.Pending > 0 || stats.Parked > 0)
                _logger.LogWarning("Search outbox daily check: tenant {TenantId} has {Pending} unconfirmed change(s) (oldest {Oldest:u}) and {Parked} that ran out of attempts.",
                    tenantId, stats.Pending, stats.OldestPendingUtc, stats.Parked);

            if (stats.Parked > 0)
            {
                var n = await outbox.ReleaseParkedAsync(_options.MaxAttempts, token);
                Interlocked.Add(ref released, n);
            }

            // Field jobs that gave up get another round too (a report keeps reading their field from SQL until they finish).
            if (scope.ServiceProvider.GetService<ISearchFieldJobs>() is { } fieldJobs)
            {
                var jobsReleased = await fieldJobs.ReleaseParkedAsync(_options.MaxAttempts, token);
                if (jobsReleased > 0)
                    _logger.LogWarning("Search field jobs daily check: tenant {TenantId} had {Count} job(s) that ran out of attempts; released for another round.", tenantId, jobsReleased);
            }
        }, ct);

        _state?.SetOutbox(totalPending, totalParked, oldest);
        _lastStatsUtc = DateTime.UtcNow;
        _logger.LogInformation("Search outbox daily check finished: {Pending} unconfirmed, {Parked} had given up ({Released} released for another round).", totalPending, totalParked, released);
    }
}
