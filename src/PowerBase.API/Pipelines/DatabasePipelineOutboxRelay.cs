using System;
using System.Collections;
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
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Pipelines;

namespace PowerBase.API.Pipelines;

public class DatabasePipelineOutboxRelay : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IControlConnectionFactory _controlConnFactory;
    private readonly ITenantConnectionResolver _tenantResolver;
    private readonly PipelineExecutionOptions _options;
    private readonly ILogger<DatabasePipelineOutboxRelay> _logger;
    private readonly string _workerId;

    public DatabasePipelineOutboxRelay(
        IServiceProvider serviceProvider,
        IControlConnectionFactory controlConnFactory,
        ITenantConnectionResolver tenantResolver,
        IOptions<PipelineExecutionOptions> options,
        ILogger<DatabasePipelineOutboxRelay> logger)
    {
        _serviceProvider = serviceProvider;
        _controlConnFactory = controlConnFactory;
        _tenantResolver = tenantResolver;
        _options = options.Value;
        _logger = logger;
        _workerId = $"db_relay_{Guid.NewGuid()}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Database Pipeline Outbox Relay started. Worker ID: {WorkerId}", _workerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Await wake notifier signal or timeout
                await Task.WhenAny(
                    PipelineOutboxWakeNotifier.WaitForOutboxItemAsync(stoppingToken),
                    Task.Delay(TimeSpan.FromSeconds(_options.DatabaseQueue.RelayPollingIntervalSeconds), stoppingToken)
                );

                if (stoppingToken.IsCancellationRequested) break;

                await ProcessRelayAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred in database outbox relay main loop.");
                await Task.Delay(2000, stoppingToken);
            }
        }
    }

    private async Task ProcessRelayAsync(CancellationToken ct)
    {
        // 1. Get all active and ready tenants from Control DB
        List<TenantInfo> tenants = [];
        try
        {
            await using var conn = _controlConnFactory.Create();
            await conn.OpenAsync(ct);
            var query = await conn.QueryAsync<(long Id, Guid PublicId)>(
                new CommandDefinition(
                    "SELECT Id, PublicId FROM meta.Tenant WHERE IsDeleted = 0 AND ProvisioningState = 'Ready'",
                    cancellationToken: ct));
            tenants = query.Select(t => new TenantInfo(t.Id, t.PublicId)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Relay failed to query active tenants from Control DB.");
            return;
        }

        bool enqueuedAny = false;

        // 2. Relay outbox items for each tenant
        foreach (var tenant in tenants)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                using var scope = _serviceProvider.CreateScope();
                
                // Configure scoped query context
                var queryContext = scope.ServiceProvider.GetRequiredService<IQueryContext>();
                queryContext.SetTenantId(tenant.Id);

                var pipelineRepo = scope.ServiceProvider.GetRequiredService<IPipelineRepository>();
                var mainQueueRepo = scope.ServiceProvider.GetRequiredService<IMainPipelineQueueRepository>();
                
                // Phase 1: Claim Transaction (commits lease immediately in Tenant DB)
                IReadOnlyList<PipelineOutboxItem> claimed = await pipelineRepo.ClaimOutboxItemsAsync(_workerId, ct);
                if (claimed.Count == 0) continue;

                _logger.LogInformation("Relay Worker {WorkerId} claimed {Count} outbox items for tenant {TenantId}.", _workerId, claimed.Count, tenant.Id);

                foreach (var item in claimed)
                {
                    if (ct.IsCancellationRequested) break;

                    try
                    {
                        // Resolve stable PipelinePublicId from Tenant DB
                        var pipeline = await pipelineRepo.GetByIdAsync(item.PipelineId, ct);
                        if (pipeline == null)
                        {
                            // Pipeline no longer exists, mark as skipped
                            await pipelineRepo.UpdateOutboxItemStatusAsync(
                                item.Id, _workerId, 2, failedOn: DateTime.UtcNow, error: $"Pipeline with ID {item.PipelineId} not found.", ct: ct);
                            continue;
                        }

                        // Map outbox item to main DB queue DTO
                        var mainQueueJob = TenantPipelinePayloadMapper.MapFromOutbox(item, tenant.Id, tenant.PublicId, pipeline.PublicId);

                        // Phase 2: Main DB insertion (outside Tenant DB transaction)
                        try
                        {
                            await mainQueueRepo.EnqueueAsync(mainQueueJob, transaction: null, ct: ct);
                            
                            // Phase 3: Tenant DB status completion marker update (short transaction)
                            await pipelineRepo.UpdateOutboxItemStatusAsync(item.Id, _workerId, 1, publishedOn: DateTime.UtcNow, ct: ct);
                            enqueuedAny = true;
                        }
                        catch (Exception dbEx)
                        {
                            // Handle duplicate MessageId in case of a retry failure
                            var existing = await mainQueueRepo.GetByMessageIdAsync(item.MessageId, ct);
                            if (existing != null)
                            {
                                // Match validation check
                                var payloadHash = PayloadHashHelper.ComputeHash(item.TriggerPayloadJson);
                                bool matches = existing.TenantId == tenant.Id &&
                                               existing.PipelinePublicId == pipeline.PublicId &&
                                               StructuralComparisons.StructuralEqualityComparer.Equals(existing.PayloadHash, payloadHash);

                                if (matches)
                                {
                                    // Verified duplicate relay success: mark outbox item published
                                    await pipelineRepo.UpdateOutboxItemStatusAsync(item.Id, _workerId, 1, publishedOn: DateTime.UtcNow, ct: ct);
                                    _logger.LogWarning("Caught duplicate MessageId {MessageId} in Main DB. Verified identities match; marked Tenant outbox item relayed.", item.MessageId);
                                    enqueuedAny = true;
                                }
                                else
                                {
                                    // Collision / mismatch: mark outbox permanently failed
                                    await pipelineRepo.UpdateOutboxItemStatusAsync(
                                        item.Id, _workerId, 2, failedOn: DateTime.UtcNow, error: "Critical Payload Hash mismatch on duplicate MessageId.", ct: ct);
                                    _logger.LogError("Critical collision: Duplicate MessageId {MessageId} found with mismatched payload/tenant signatures.", item.MessageId);
                                }
                            }
                            else
                            {
                                // Normal database failure (e.g. timeout), log and allow outbox lease to expire for retry
                                _logger.LogError(dbEx, "Main DB Enqueue failed for MessageId {MessageId}. Relay lease will time out.", item.MessageId);
                            }
                        }
                    }
                    catch (Exception itemEx)
                    {
                        _logger.LogError(itemEx, "Failed to relay outbox item {Id} for tenant {TenantId}.", item.Id, tenant.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Relay failed processing for tenant {TenantId}.", tenant.Id);
            }
        }

        // Wake worker if new jobs enqueued
        if (enqueuedAny)
        {
            DatabasePipelineQueueWakeNotifier.Wake();
        }
    }

    private sealed record TenantInfo(long Id, Guid PublicId);
}
