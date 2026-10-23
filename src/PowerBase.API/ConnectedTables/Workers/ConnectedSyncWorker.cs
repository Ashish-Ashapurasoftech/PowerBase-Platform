using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.ConnectedTables;
using PowerBase.Application.ConnectedTables.Interfaces;
using PowerBase.Application.ConnectedTables.Sync;
using PowerBase.Domain.ConnectedTables;
using PowerBase.Domain.Constants;
using PowerBase.Infrastructure.ConnectedTables;

namespace PowerBase.API.ConnectedTables.Workers;

/// <summary>Runs queued connected-table refreshes in the background. Leases items from the control-DB queue (never more than
/// the configured number per source realm, so many consumers can't overload one source database), keeps the lease alive while
/// a refresh runs, and puts transiently failed or busy items back with a growing delay.</summary>
public sealed class ConnectedSyncWorker(IServiceProvider services, IOptions<ConnectedTablesOptions> options, ILogger<ConnectedSyncWorker> logger)
    : BackgroundService
{
    private const int LeaseSeconds = 120;
    private const int HeartbeatSeconds = 30;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly string _workerId = $"connected_worker_{Guid.NewGuid():N}";
    private readonly SemaphoreSlim _slots = new(Math.Max(1, options.Value.MaxConcurrentRuns), Math.Max(1, options.Value.MaxConcurrentRuns));
    private readonly ConcurrentDictionary<long, Task> _active = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConnectedQueueWakeNotifier.WaitAsync(PollInterval, stoppingToken);
                var free = _slots.CurrentCount;
                if (free == 0) continue;

                using var scope = services.CreateScope();
                var claimed = await scope.ServiceProvider.GetRequiredService<IConnectedRunQueue>()
                    .ClaimAsync(_workerId, free, LeaseSeconds, options.Value.MaxConcurrentPerProvider, stoppingToken);
                foreach (var item in claimed)
                {
                    await _slots.WaitAsync(stoppingToken);
                    _active[item.Id] = RunAsync(item, stoppingToken);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Connected-table worker loop failed.");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
        await Task.WhenAny(Task.WhenAll(_active.Values), Task.Delay(TimeSpan.FromSeconds(15)));
    }

    private async Task RunAsync(ConnectedQueueItem item, CancellationToken stopping)
    {
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var heartbeat = HeartbeatAsync(item, heartbeatCts.Token);
        var outcome = ConnectedRunOutcome.Done;
        string? error = null;
        try
        {
            using var scope = services.CreateScope();
            var sp = scope.ServiceProvider;
            var context = sp.GetRequiredService<IQueryContext>();
            context.SetTenantId(item.TenantId);

            var runs = sp.GetRequiredService<IConnectedRunRepository>();
            var run = await runs.GetByPublicIdAsync(item.RunPublicId, stopping);
            if (run is null) { error = "Run not found."; return; }
            var table = await sp.GetRequiredService<IConnectedTableRepository>().GetByIdAsync(run.ConnectedTableId, stopping);
            if (table is null)
            {
                await runs.CompleteAsync(run, ConnectedRunStatuses.Failed, "The connected table no longer exists.", stopping);
                return;
            }

            // Destination writes are made as the connection owner, who must still be an active user.
            var owner = await sp.GetRequiredService<IUserRepository>().GetByIdAsync(table.OwnerUserId, stopping);
            if (!owner.IsActive || owner.IsDeleted)
            {
                const string reason = "The owner of this table's connection is no longer active. An app administrator can switch the table to their own connection.";
                await runs.CompleteAsync(run, ConnectedRunStatuses.Failed, reason, stopping);
                await sp.GetRequiredService<IConnectedTableRepository>().SetStatusAsync(table.Id, ConnectedTableStatuses.NeedsAttention, reason, stopping);
                return;
            }
            context.SetUserIdentity(owner.Id, owner.SystemRoleCode == SystemRoleCodes.SuperAdmin, owner.Name, owner.Email, new HashSet<string>(), string.Empty);

            outcome = await sp.GetRequiredService<ConnectedSyncProcessor>().RunAsync(item.RunPublicId, item.AttemptCount, stopping);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { error = "Worker shutting down."; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Connected refresh {RunId} crashed.", item.RunPublicId);
            error = ex.Message.Length > 900 ? ex.Message[..900] : ex.Message;
        }
        finally
        {
            heartbeatCts.Cancel();
            await heartbeat;
            try
            {
                using var scope = services.CreateScope();
                var queue = scope.ServiceProvider.GetRequiredService<IConnectedRunQueue>();
                // On shutdown the lease is left to expire, so the run is picked up again (and starts over) after a restart.
                if (!stopping.IsCancellationRequested)
                {
                    if (outcome == ConnectedRunOutcome.RetryLater)
                        await queue.RetryLaterAsync(item.Id, _workerId, item.ClaimToken, TimeSpan.FromSeconds(Math.Min(600, 30 * item.AttemptCount)), "Retry", CancellationToken.None);
                    else
                        await queue.CompleteAsync(item.Id, _workerId, item.ClaimToken, error, CancellationToken.None);
                }
            }
            catch (Exception ex) { logger.LogWarning(ex, "Could not finalize connected queue item {Id}.", item.Id); }
            _slots.Release();
            _active.TryRemove(item.Id, out _);
        }
    }

    private async Task HeartbeatAsync(ConnectedQueueItem item, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(HeartbeatSeconds), ct);
                using var scope = services.CreateScope();
                var renewed = await scope.ServiceProvider.GetRequiredService<IConnectedRunQueue>()
                    .RenewLeaseAsync(item.Id, _workerId, item.ClaimToken, LeaseSeconds, ct);
                if (!renewed) logger.LogWarning("Lost the lease for connected queue item {Id}.", item.Id);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogWarning(ex, "Connected heartbeat failed for queue item {Id}.", item.Id); }
    }
}
