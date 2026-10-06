using System.Collections.Concurrent;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Domain.Constants;
using PowerBase.Infrastructure.Imports;
using PowerBase.Infrastructure.Services;

namespace PowerBase.API.Workers;

/// <summary>Runs queued import runs in the background so users never wait on screen. Leases runs from the control
/// queue, keeps the lease alive while a run executes, and limits concurrency per tenant and overall so a few large
/// imports cannot starve the rest of the platform.</summary>
public sealed class ImportExecutionWorker(IServiceProvider services, ILogger<ImportExecutionWorker> logger) : BackgroundService
{
    private const int GlobalLimit = 4;
    private const int PerTenantLimit = 2;
    private const int LeaseSeconds = 120;
    private const int HeartbeatSeconds = 30;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly string _workerId = $"import_worker_{Guid.NewGuid():N}";
    private readonly SemaphoreSlim _global = new(GlobalLimit, GlobalLimit);
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _tenants = new();
    private readonly ConcurrentDictionary<long, Task> _active = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ImportQueueWakeNotifier.WaitAsync(PollInterval, stoppingToken);
                var free = _global.CurrentCount;
                if (free == 0) continue;

                using var scope = services.CreateScope();
                var claimed = await scope.ServiceProvider.GetRequiredService<IImportQueue>().ClaimAsync(_workerId, free, LeaseSeconds, stoppingToken);
                foreach (var item in claimed) _active[item.Id] = RunAsync(item, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Import worker loop failed.");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
        await Task.WhenAny(Task.WhenAll(_active.Values), Task.Delay(TimeSpan.FromSeconds(15)));
    }

    private async Task RunAsync(ImportQueueItem item, CancellationToken stopping)
    {
        var tenantGate = _tenants.GetOrAdd(item.TenantId, _ => new SemaphoreSlim(PerTenantLimit, PerTenantLimit));
        // The heartbeat starts before waiting for a slot so a queued run's lease cannot expire while it waits.
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var heartbeat = HeartbeatAsync(item, heartbeatCts.Token);
        var holdsSlots = false;
        string? error = null;
        try
        {
            await tenantGate.WaitAsync(stopping);
            await _global.WaitAsync(stopping);
            holdsSlots = true;
            using var scope = services.CreateScope();
            var sp = scope.ServiceProvider;
            var context = (QueryContext)sp.GetRequiredService<IQueryContext>();
            context.SetTenantId(item.TenantId);

            var run = await sp.GetRequiredService<IImportRunRepository>().GetByPublicIdAsync(item.RunPublicId, stopping);
            if (run is null) { error = "Run not found."; return; }
            var user = await sp.GetRequiredService<IUserRepository>().GetByIdAsync(run.TriggeredByUserId, stopping);
            if (!user.IsActive || user.IsDeleted)
            {
                await sp.GetRequiredService<IImportRunRepository>().CompleteAsync(run.Id, ImportRunStatus.Failed, "The user who started this import is no longer active.", ct: stopping);
                return;
            }
            context.SetUserIdentity(user.Id, user.SystemRoleCode == SystemRoleCodes.SuperAdmin, user.Name, user.Email,
                new HashSet<string>(), string.Empty);

            await sp.GetRequiredService<ImportRunProcessor>().RunAsync(item.RunPublicId, stopping);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { error = "Worker shutting down."; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Import run {RunId} crashed.", item.RunPublicId);
            error = ex.Message.Length > 900 ? ex.Message[..900] : ex.Message;
        }
        finally
        {
            heartbeatCts.Cancel();
            await heartbeat;
            try
            {
                using var scope = services.CreateScope();
                var queue = scope.ServiceProvider.GetRequiredService<IImportQueue>();
                // A shutdown leaves the lease to expire so another worker (or this one after restart) picks the run up.
                if (!stopping.IsCancellationRequested) await queue.CompleteAsync(item.Id, _workerId, item.ClaimToken, error, CancellationToken.None);
            }
            catch (Exception ex) { logger.LogWarning(ex, "Could not finalize import queue item {Id}.", item.Id); }
            if (holdsSlots) { _global.Release(); tenantGate.Release(); }
            _active.TryRemove(item.Id, out _);
            ImportQueueWakeNotifier.Wake();
        }
    }

    private async Task HeartbeatAsync(ImportQueueItem item, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(HeartbeatSeconds), ct);
                using var scope = services.CreateScope();
                var renewed = await scope.ServiceProvider.GetRequiredService<IImportQueue>()
                    .RenewLeaseAsync(item.Id, _workerId, item.ClaimToken, LeaseSeconds, ct);
                if (!renewed) logger.LogWarning("Lost the lease for import queue item {Id}.", item.Id);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogWarning(ex, "Import heartbeat failed for queue item {Id}.", item.Id); }
    }
}
