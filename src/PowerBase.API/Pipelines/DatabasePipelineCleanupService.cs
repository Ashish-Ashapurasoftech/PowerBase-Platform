using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.API.Pipelines;

public class DatabasePipelineCleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly PipelineExecutionOptions _options;
    private readonly ILogger<DatabasePipelineCleanupService> _logger;

    public DatabasePipelineCleanupService(
        IServiceProvider serviceProvider,
        IOptions<PipelineExecutionOptions> options,
        ILogger<DatabasePipelineCleanupService> logger)
    {
        _serviceProvider = serviceProvider;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Database Pipeline Queue Cleanup Service started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Run pruning run immediately on startup, then wait configured hours
                await PruneTerminalRecordsAsync(stoppingToken);
                
                await Task.Delay(TimeSpan.FromHours(_options.DatabaseQueue.CleanupIntervalHours), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred in Database Pipeline Queue Cleanup hosted service.");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); // retry delay on failure
            }
        }
    }

    private async Task PruneTerminalRecordsAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMainPipelineQueueRepository>();
        
        int batchesRun = 0;
        int rowsDeleted = 0;
        var batchSize = _options.DatabaseQueue.CleanupBatchSize;
        var maxBatches = _options.DatabaseQueue.CleanupMaxBatchesPerRun;
        var retentionDays = _options.DatabaseQueue.RetentionDays;

        _logger.LogInformation("Starting queue table cleanup. Pruning terminal records older than {Retention} days...", retentionDays);

        do
        {
            var deleted = await repo.PruneQueueBatchAsync(retentionDays, batchSize, ct);
            rowsDeleted += deleted;
            batchesRun++;

            if (deleted < batchSize || batchesRun >= maxBatches)
            {
                break;
            }

            await Task.Delay(500, ct); // Cool-down delay to protect Main DB from lock escalation
        } while (batchesRun < maxBatches && !ct.IsCancellationRequested);

        _logger.LogInformation("Completed queue pruning. Deleted {Count} total terminal queue rows across {Batches} batches.", rowsDeleted, batchesRun);

        // 2. Prune tenant bulk event staging tables
        _logger.LogInformation("Starting bulk event staging record pruning across all active tenants...");
        List<(long Id, Guid PublicId)> tenants = [];
        try
        {
            var controlConnFactory = scope.ServiceProvider.GetRequiredService<IControlConnectionFactory>();
            await using var conn = controlConnFactory.Create();
            await conn.OpenAsync(ct);
            var query = await conn.QueryAsync<(long Id, Guid PublicId)>(
                new CommandDefinition("SELECT Id, PublicId FROM meta.Tenant WHERE IsDeleted = 0 AND ProvisioningState = 'Ready'", cancellationToken: ct));
            tenants = query.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pruning service failed to query active tenants from Control DB.");
            return;
        }

        var olderThan = DateTime.UtcNow.AddDays(-retentionDays);
        foreach (var tenant in tenants)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                using var tenantScope = _serviceProvider.CreateScope();
                var queryContext = tenantScope.ServiceProvider.GetRequiredService<IQueryContext>();
                queryContext.SetTenantId(tenant.Id);

                var pipelineRepo = tenantScope.ServiceProvider.GetRequiredService<IPipelineRepository>();
                await pipelineRepo.DeleteExpiredBulkEventRecordsAsync(olderThan, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to prune bulk event records for tenant {TenantId}.", tenant.Id);
            }
        }
    }
}
