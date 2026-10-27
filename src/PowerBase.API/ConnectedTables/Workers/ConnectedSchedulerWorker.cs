using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.ConnectedTables.Interfaces;
using PowerBase.Application.ConnectedTables.Scheduling;
using PowerBase.Application.ConnectedTables.Sync;
using PowerBase.Domain.ConnectedTables;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.API.ConnectedTables.Workers;

/// <summary>Starts connected-table refreshes on their schedule. Once a minute it asks every tenant which connected tables are
/// due, queues a refresh for each (as the connection owner) and works out the next run time. A per-tenant database lock keeps
/// two API instances from queueing the same occurrence twice.</summary>
public sealed class ConnectedSchedulerWorker(IControlConnectionFactory control, IServiceProvider services, ILogger<ConnectedSchedulerWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, stoppingToken);
                await EvaluateAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "Connected-table scheduler loop failed."); }
        }
    }

    private async Task EvaluateAsync(CancellationToken ct)
    {
        List<long> tenantIds;
        await using (var conn = control.Create())
        {
            await conn.OpenAsync(ct);
            tenantIds = (await conn.QueryAsync<long>(new CommandDefinition(
                "SELECT Id FROM meta.Tenant WHERE IsDeleted = 0 AND ProvisioningState = 'Ready'", cancellationToken: ct))).ToList();
        }

        foreach (var tenantId in tenantIds)
        {
            if (ct.IsCancellationRequested) break;
            try { await EvaluateTenantAsync(tenantId, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            // A tenant whose database predates the module (no 'connected' schema yet) simply has nothing due.
            catch (Exception ex) { logger.LogDebug(ex, "Connected-table scheduling skipped tenant {TenantId}.", tenantId); }
        }
    }

    private async Task EvaluateTenantAsync(long tenantId, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IQueryContext>().SetTenantId(tenantId);

        await using var lockConn = await scope.ServiceProvider.GetRequiredService<ITenantConnectionFactory>().CreateAsync(ct);
        await lockConn.OpenAsync(ct);
        var locked = await lockConn.QuerySingleAsync<int>(
            "DECLARE @res INT; EXEC @res = sp_getapplock @Resource = @name, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @res;",
            new { name = $"PB_ConnectedScheduler_Tenant_{tenantId}" });
        if (locked < 0) return;

        var tables = scope.ServiceProvider.GetRequiredService<IConnectedTableRepository>();
        var runs = scope.ServiceProvider.GetRequiredService<IConnectedRunRepository>();
        var starter = scope.ServiceProvider.GetRequiredService<ConnectedRunStarter>();
        var now = DateTime.UtcNow;
        foreach (var table in await tables.ListDueAsync(now, 200, ct))
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                // A refresh still queued or running from the last occurrence: skip this one rather than pile up.
                if (await runs.GetActiveForTableAsync(table.Id, ct) is null)
                    await starter.QueueAsync(table, ConnectedRunTriggers.Schedule, table.OwnerUserId, confirmMassDelete: false, ct);
            }
            catch (ConnectedTableException) { /* already refreshing */ }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Scheduled refresh of connected table {Id} could not be queued.", table.PublicId);
            }
            await tables.SetNextRunAsync(table.Id, ConnectedScheduling.NextRun(ConnectedScheduling.Parse(table.ScheduleJson), table.TimeZone, now), ct);
        }
    }
}
