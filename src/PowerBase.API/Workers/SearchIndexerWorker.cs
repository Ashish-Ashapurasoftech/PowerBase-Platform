using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;

namespace PowerBase.API.Workers;

public class SearchIndexerWorker : BackgroundService
{
    private readonly ServiceBusClient _client;
    private ServiceBusReceiver _receiver;
    private readonly string _queueName;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SearchIndexerWorker> _logger;
    private const int MaxBatchSize = 250;

    public SearchIndexerWorker(IConfiguration configuration, IServiceProvider serviceProvider, ILogger<SearchIndexerWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        
        var connectionString = configuration["AzureServiceBus:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString) || connectionString.StartsWith("<") || !connectionString.Contains("Endpoint="))
        {
            connectionString = "Endpoint=sb://mock.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=mock";
        }
        _queueName = configuration["AzureServiceBus:SearchIndexQueue"] ?? "search-indexing-queue";

        _client = new ServiceBusClient(connectionString);
        _receiver = _client.CreateReceiver(_queueName, new ServiceBusReceiverOptions
        {
            PrefetchCount = MaxBatchSize
        });
    }

    private int _consecutiveFullBatches = 0;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try 
            {
                // D10: Check if Azure AI Search is enabled. If not, pause without draining queue messages.
                using (var checkScope = _serviceProvider.CreateScope())
                {
                    var searchService = checkScope.ServiceProvider.GetRequiredService<IAzureSearchService>();
                    if (!searchService.IsEnabled)
                    {
                        _logger.LogWarning("Azure AI Search is disabled in configuration. SearchIndexerWorker will pause and not drain messages.");
                        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                        continue;
                    }
                }

                // D01: Recreate receiver if closed
                if (_receiver.IsClosed)
                {
                    _receiver = _client.CreateReceiver(_queueName, new ServiceBusReceiverOptions
                    {
                        PrefetchCount = MaxBatchSize
                    });
                }

                // Wait for a batch of messages with a maximum wait time of 5 seconds to form a batch
                var messages = await _receiver.ReceiveMessagesAsync(maxMessages: MaxBatchSize, maxWaitTime: TimeSpan.FromSeconds(5), cancellationToken: stoppingToken);
                
                if (messages == null || !messages.Any())
                {
                    _consecutiveFullBatches = 0;
                    continue; // No messages, wait again
                }

                // D15: Backlog congestion detection
                if (messages.Count >= MaxBatchSize)
                {
                    _consecutiveFullBatches++;
                    if (_consecutiveFullBatches >= 3)
                    {
                        _logger.LogWarning("Search indexer queue backlog alert: received {BatchSize} messages continuously for {Count} batches. Queue backlog is active.", MaxBatchSize, _consecutiveFullBatches);
                    }
                }
                else
                {
                    _consecutiveFullBatches = 0;
                }

                await ProcessMessageBatchAsync(messages, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // D01: Per-iteration exception handling ensures worker survives transient faults
                _logger.LogError(ex, "Error in Service Bus Receiver loop. Backing off before retrying.");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        try
        {
            await _receiver.CloseAsync();
        }
        catch { }
    }

    private async Task ProcessMessageBatchAsync(IReadOnlyList<ServiceBusReceivedMessage> messages, CancellationToken stoppingToken)
    {
        using var batchScope = _serviceProvider.CreateScope();
        var searchService = batchScope.ServiceProvider.GetRequiredService<IAzureSearchService>();

        if (!searchService.IsEnabled)
        {
            _logger.LogWarning("Azure AI Search is disabled in configuration. Aborting message batch processing without completing messages.");
            return;
        }

        var documentsToIndex = new List<SearchIndexDocument>();
        var recordsToDelete = new List<(long TenantId, long TableId, Guid RecordPublicId)>();
        var messagesToComplete = new List<ServiceBusReceivedMessage>();

        foreach (var sbMessage in messages)
        {
            try
            {
                var message = JsonSerializer.Deserialize<SearchIndexMessage>(sbMessage.Body);
                if (message == null)
                {
                    messagesToComplete.Add(sbMessage); // Invalid message, discard
                    continue;
                }

                if (message.Action == IndexAction.Upsert)
                {
                    IReadOnlyDictionary<long, object?>? recordData = null;

                    if (message.Payload != null && message.Payload.Count > 0)
                    {
                        recordData = message.Payload.ToDictionary(
                            kvp => long.Parse(kvp.Key), 
                            kvp => 
                            {
                                if (kvp.Value is System.Text.Json.JsonElement el)
                                {
                                    return el.ValueKind switch
                                    {
                                        System.Text.Json.JsonValueKind.String => el.GetString(),
                                        System.Text.Json.JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.TryGetDouble(out var d) ? (object)d : el.GetRawText(),
                                        System.Text.Json.JsonValueKind.True => true,
                                        System.Text.Json.JsonValueKind.False => false,
                                        System.Text.Json.JsonValueKind.Null => null,
                                        _ => el.GetRawText()
                                    };
                                }
                                return kvp.Value;
                            }
                        );
                    }
                    else
                    {
                        using var scope = _serviceProvider.CreateScope();
                        var queryContext = scope.ServiceProvider.GetRequiredService<IQueryContext>();
                        queryContext.SetTenantId(message.TenantId);
                        var recordRepo = scope.ServiceProvider.GetRequiredService<IRecordRepository>();
                        
                        recordData = await recordRepo.GetSearchableFieldsAsync(message.TableId, message.RecordPublicId, stoppingToken);
                    }

                    if (recordData != null && recordData.Count > 0)
                    {
                        var tableSlotInfo = await GetTableSlotMapAsync(message.TableId, batchScope.ServiceProvider, stoppingToken);
                        var slotValues = new Dictionary<string, object?>();
                        foreach (var kvp in recordData)
                        {
                            if (tableSlotInfo.SlotMap.TryGetValue(kvp.Key, out var sInfo))
                            {
                                if (PowerBase.Application.Search.SearchSlotResolver.IsNumeric(sInfo.TypeCode))
                                    slotValues[sInfo.Slot] = PowerBase.Application.Search.SearchSlotResolver.ParseDouble(kvp.Value);
                                else if (PowerBase.Application.Search.SearchSlotResolver.IsDate(sInfo.TypeCode))
                                    slotValues[sInfo.Slot] = PowerBase.Application.Search.SearchSlotResolver.ParseDateTimeOffset(kvp.Value);
                                else if (PowerBase.Application.Search.SearchSlotResolver.IsBoolean(sInfo.TypeCode))
                                    slotValues[sInfo.Slot] = PowerBase.Application.Search.SearchSlotResolver.ParseBoolean(kvp.Value);
                                else
                                    slotValues[sInfo.Slot] = PowerBase.Application.Search.SearchSlotResolver.ParseString(kvp.Value);
                            }
                        }

                        documentsToIndex.Add(new SearchIndexDocument(
                            message.TenantId,
                            message.AppId,
                            message.TableId,
                            message.RecordPublicId,
                            recordData,
                            slotValues,
                            tableSlotInfo.TargetIndex
                        ));
                    }
                }
                else if (message.Action == IndexAction.Delete)
                {
                    recordsToDelete.Add((message.TenantId, message.TableId, message.RecordPublicId));
                }
                else if (message.Action == IndexAction.BackfillField || message.Action == IndexAction.NullifyField)
                {
                    using var scope = _serviceProvider.CreateScope();
                    var queryContext = scope.ServiceProvider.GetRequiredService<IQueryContext>();
                    queryContext.SetTenantId(message.TenantId);
                    var recordRepo = scope.ServiceProvider.GetRequiredService<IRecordRepository>();

                    var isNullify = message.Action == IndexAction.NullifyField;
                    if (message.FieldId.HasValue)
                    {
                        var docs = await recordRepo.GetFieldBackfillBatchAsync(message.TenantId, message.AppId, message.TableId, message.FieldId.Value, isNullify, message.Page, 500, stoppingToken);
                        if (docs.Count > 0)
                        {
                            var tableSlotInfo = await GetTableSlotMapAsync(message.TableId, batchScope.ServiceProvider, stoppingToken);
                            var slottedDocs = new List<SearchIndexDocument>(docs.Count);
                            foreach (var d in docs)
                            {
                                var slotVals = new Dictionary<string, object?>();
                                if (tableSlotInfo.SlotMap.TryGetValue(message.FieldId.Value, out var sInfo))
                                {
                                    if (d.Values.TryGetValue(message.FieldId.Value, out var val))
                                    {
                                        if (val == null || isNullify)
                                        {
                                            slotVals[sInfo.Slot] = null;
                                        }
                                        else if (PowerBase.Application.Search.SearchSlotResolver.IsNumeric(sInfo.TypeCode))
                                        {
                                            slotVals[sInfo.Slot] = PowerBase.Application.Search.SearchSlotResolver.ParseDouble(val);
                                        }
                                        else if (PowerBase.Application.Search.SearchSlotResolver.IsDate(sInfo.TypeCode))
                                        {
                                            slotVals[sInfo.Slot] = PowerBase.Application.Search.SearchSlotResolver.ParseDateTimeOffset(val);
                                        }
                                        else if (PowerBase.Application.Search.SearchSlotResolver.IsBoolean(sInfo.TypeCode))
                                        {
                                            slotVals[sInfo.Slot] = PowerBase.Application.Search.SearchSlotResolver.ParseBoolean(val);
                                        }
                                        else
                                        {
                                            slotVals[sInfo.Slot] = PowerBase.Application.Search.SearchSlotResolver.ParseString(val);
                                        }
                                    }
                                }

                                slottedDocs.Add(new SearchIndexDocument(
                                    d.TenantId,
                                    d.AppId,
                                    d.TableId,
                                    d.PublicId,
                                    d.Values,
                                    slotVals,
                                    tableSlotInfo.TargetIndex
                                ));
                            }

                            documentsToIndex.AddRange(slottedDocs);
                            
                            // Send continuation message
                            if (docs.Count == 500)
                            {
                                var nextMsg = new SearchIndexMessage
                                {
                                    Action = message.Action,
                                    TenantId = message.TenantId,
                                    AppId = message.AppId,
                                    TableId = message.TableId,
                                    FieldId = message.FieldId,
                                    Page = message.Page + 1
                                };
                                var publisher = scope.ServiceProvider.GetRequiredService<IMessagePublisher>();
                                await publisher.PublishAsync(nextMsg, stoppingToken);
                            }
                        }
                    }
                }

                messagesToComplete.Add(sbMessage);
            }
            catch (Exception ex)
            {
                if (sbMessage.DeliveryCount > 5)
                {
                    _logger.LogCritical(ex, "Search index message {MessageId} failed processing {Count} times. Moving to DLQ.", sbMessage.MessageId, sbMessage.DeliveryCount);
                    await _receiver.DeadLetterMessageAsync(sbMessage, "ProcessingError", ex.Message, stoppingToken);
                }
                else
                {
                    _logger.LogError(ex, "Error processing individual search index message in batch. DeliveryCount: {Count}", sbMessage.DeliveryCount);
                }
            }
        }

        try
        {
            // Execute bulk actions for the current batch
            if (documentsToIndex.Any())
            {
                await searchService.BulkIndexRecordsAsync(documentsToIndex, stoppingToken);
            }

            if (recordsToDelete.Any())
            {
                var deleteGroups = recordsToDelete.GroupBy(x => new { x.TenantId, x.TableId });
                foreach (var group in deleteGroups)
                {
                    await searchService.BulkDeleteRecordsAsync(group.Key.TenantId, group.Key.TableId, group.Select(x => x.RecordPublicId).ToList(), stoppingToken);
                }
            }

            // Complete successfully processed messages
            foreach (var sbMessage in messagesToComplete)
            {
                try
                {
                    await _receiver.CompleteMessageAsync(sbMessage, stoppingToken);
                }
                catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessageLockLost)
                {
                    _logger.LogWarning("Message lock lost for {MessageId}; message will be redelivered.", sbMessage.MessageId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pushing bulk batch to Azure AI Search. Entire batch will be retried.");
            
            // GAP #7: Monitor and explicitly dead-letter messages that fail continuously during bulk operations
            foreach (var sbMessage in messages)
            {
                if (sbMessage.DeliveryCount > 5 && !messagesToComplete.Contains(sbMessage))
                {
                    _logger.LogCritical(ex, "Search index message {MessageId} failed bulk upload {Count} times. Moving to DLQ.", sbMessage.MessageId, sbMessage.DeliveryCount);
                    await _receiver.DeadLetterMessageAsync(sbMessage, "BulkUploadFailed", ex.Message, stoppingToken);
                }
            }
        }
    }

    public record TableSlotInfo(Dictionary<long, (string Slot, string TypeCode)> SlotMap, bool IsWide, string? TargetIndex);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, TableSlotInfo> _tableSlotCache = new();

    private async Task<TableSlotInfo> GetTableSlotMapAsync(long tableId, IServiceProvider sp, CancellationToken ct)
    {
        if (_tableSlotCache.TryGetValue(tableId, out var existing)) return existing;
        try
        {
            var fieldRepo = sp.GetRequiredService<IAppFieldRepository>();
            var fields = await fieldRepo.ListByTableAsync(tableId, ct);
            var isWide = PowerBase.Application.Search.SearchSlotResolver.IsWideTable(fields);
            var targetIndex = isWide ? $"pb-tbl-{tableId}" : null;
            var slotMap = PowerBase.Application.Search.SearchSlotResolver.BuildSlotMap(fields);
            var result = fields
                .Where(f => f.Fid.HasValue && slotMap.ContainsKey((long)f.Fid.Value))
                .ToDictionary(f => (long)f.Fid!.Value, f => (slotMap[(long)f.Fid!.Value], f.TypeCode));
            var info = new TableSlotInfo(result, isWide, targetIndex);
            _tableSlotCache[tableId] = info;
            return info;
        }
        catch
        {
            return new(new(), false, null);
        }
    }
}
