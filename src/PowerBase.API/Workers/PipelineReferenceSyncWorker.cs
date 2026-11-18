using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.FieldReferences;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Services;

namespace PowerBase.API.Workers;

/// <summary>Keeps the Usage index of PowerFlows current. PowerFlows are saved by another module, which this does not touch: every
/// minute it asks each tenant for the PowerFlows created, edited or deleted since the last scan and re-indexes just those. The Usage
/// tab runs the same scan before it answers, so this only keeps that scan short. Re-indexing is idempotent, so two API instances
/// scanning the same tenant at once do no harm.</summary>
public sealed class PipelineReferenceSyncWorker(
    IControlConnectionFactory control, IServiceProvider services, ILogger<PipelineReferenceSyncWorker> logger)
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
                await SyncAllTenantsAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "PowerFlow field-reference sync loop failed."); }
        }
    }

    private async Task SyncAllTenantsAsync(CancellationToken ct)
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
            try
            {
                using var scope = services.CreateScope();
                ((QueryContext)scope.ServiceProvider.GetRequiredService<IQueryContext>()).SetTenantId(tenantId);
                var count = await scope.ServiceProvider.GetRequiredService<IFieldReferenceIndexer>().SyncPipelinesAsync(ct);
                if (count > 0) logger.LogInformation("Re-indexed {Count} PowerFlows for tenant {TenantId}.", count, tenantId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "PowerFlow field-reference sync failed for tenant {TenantId}.", tenantId); }
        }
    }
}
