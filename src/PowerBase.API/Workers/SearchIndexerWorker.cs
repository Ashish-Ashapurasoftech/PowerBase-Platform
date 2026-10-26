using System;
using System.Collections.Concurrent;
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
using PowerBase.Application.Search;

namespace PowerBase.API.Workers;

public class SearchIndexerWorker : BackgroundService
{
    protected readonly ServiceBusClient _client;
    protected ServiceBusReceiver _receiver;
    protected readonly string _queueName;
    protected readonly IServiceProvider _serviceProvider;
    protected readonly ILogger _logger;
    protected readonly int _maxBatchSize;
    protected readonly TimeSpan _maxWaitTime;

    /// <summary>How long a table's slot map is trusted. Slots themselves never move once stored, so this only bounds how
    /// long a worker can miss a field that was just added or switched on.</summary>
    internal static readonly TimeSpan SlotMapTtl = TimeSpan.FromSeconds(15);

    public SearchIndexerWorker(IConfiguration configuration, IServiceProvider serviceProvider, ILogger<SearchIndexerWorker> logger)
        : this(
            configuration,
            serviceProvider,
            logger,
            configuration["AzureServiceBus:SearchIndexQueue"] ?? "search-indexing-queue",
            maxBatchSize: 500,
            maxWaitTime: TimeSpan.FromSeconds(5))
    {
    }

    protected SearchIndexerWorker(
        IConfiguration configuration,
        IServiceProvider serviceProvider,
        ILogger logger,
        string queueName,
        int maxBatchSize,
        TimeSpan maxWaitTime)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _queueName = queueName;
        _maxBatchSize = maxBatchSize;
        _maxWaitTime = maxWaitTime;

        var connectionString = configuration["AzureServiceBus:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString) || connectionString.StartsWith("<") || !connectionString.Contains("Endpoint="))
        {
            connectionString = "Endpoint=sb://mock.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=mock";
        }

        _client = new ServiceBusClient(connectionString);
        _receiver = _client.CreateReceiver(_queueName, new ServiceBusReceiverOptions
        {
            PrefetchCount = _maxBatchSize
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
                        _logger.LogWarning("Azure AI Search is disabled in configuration. {WorkerName} on {QueueName} will pause and not drain messages.", GetType().Name, _queueName);
                        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                        continue;
                    }
                }

                // D01: Recreate receiver if closed
                if (_receiver.IsClosed)
                {
                    _receiver = _client.CreateReceiver(_queueName, new ServiceBusReceiverOptions
                    {
                        PrefetchCount = _maxBatchSize
                    });
                }

                // Wait for a batch of messages
                var messages = await _receiver.ReceiveMessagesAsync(maxMessages: _maxBatchSize, maxWaitTime: _maxWaitTime, cancellationToken: stoppingToken);

                if (messages == null || !messages.Any())
                {
                    _consecutiveFullBatches = 0;
                    continue; // No messages, wait again
                }

                // D15: Backlog congestion detection
                if (messages.Count >= _maxBatchSize)
                {
                    _consecutiveFullBatches++;
                    if (_consecutiveFullBatches >= 3)
                    {
                        _logger.LogWarning("{WorkerName} queue backlog alert: received {BatchSize} messages continuously for {Count} batches on {QueueName}. Queue backlog is active.", GetType().Name, _maxBatchSize, _consecutiveFullBatches, _queueName);
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

    /// <summary>Which message(s) produced a document, so a document Azure rejects can be traced back to its message.</summary>
    private sealed record DocOwner(ServiceBusReceivedMessage Message, long TenantId, long TableId);

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
        var docOwners = new Dictionary<string, List<DocOwner>>(StringComparer.OrdinalIgnoreCase);
        var recordsToDelete = new List<(long TenantId, long TableId, Guid RecordPublicId)>();
        var messagesToComplete = new List<ServiceBusReceivedMessage>();
        var settled = new HashSet<string>(); // messages already dead-lettered, so nothing settles a message twice

        void AddDocument(ServiceBusReceivedMessage message, SearchIndexDocument doc)
        {
            documentsToIndex.Add(doc);
            var key = doc.PublicId.ToString();
            if (!docOwners.TryGetValue(key, out var owners)) docOwners[key] = owners = new List<DocOwner>();
            owners.Add(new DocOwner(message, doc.TenantId, doc.TableId));
        }

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
                        var info = await GetTableSlotInfoAsync(message.TenantId, message.TableId, recordData.Keys, stoppingToken);
                        var slotValues = new Dictionary<string, object?>();
                        foreach (var kvp in recordData)
                        {
                            if (info.SlotMap.TryGetValue(kvp.Key, out var slot))
                                slotValues[slot.Slot] = SearchSlotResolver.ToSlotValue(slot.TypeCode, kvp.Value);
                        }

                        AddDocument(sbMessage, new SearchIndexDocument(
                            message.TenantId,
                            message.AppId,
                            message.TableId,
                            message.RecordPublicId,
                            recordData,
                            slotValues,
                            info.TargetIndex,
                            info.SearchTextSlots));
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
                        var fid = message.FieldId.Value;
                        var docs = await recordRepo.GetFieldBackfillBatchAsync(message.TenantId, message.AppId, message.TableId, fid, isNullify, message.Page, 500, stoppingToken);
                        if (docs.Count > 0)
                        {
                            var info = await GetTableSlotInfoAsync(message.TenantId, message.TableId, [fid], stoppingToken);
                            // A field that was just switched off is no longer in SlotMap, but its stored slot still has to be cleared.
                            var hasSlot = info.SlotMap.TryGetValue(fid, out var slot) || (isNullify && info.StoredSlots.TryGetValue(fid, out slot));
                            foreach (var d in docs)
                            {
                                var slotVals = new Dictionary<string, object?>();
                                if (hasSlot)
                                {
                                    if (isNullify) slotVals[slot.Slot] = null;
                                    else if (d.Values.TryGetValue(fid, out var val)) slotVals[slot.Slot] = SearchSlotResolver.ToSlotValue(slot.TypeCode, val);
                                }

                                AddDocument(sbMessage, new SearchIndexDocument(
                                    d.TenantId,
                                    d.AppId,
                                    d.TableId,
                                    d.PublicId,
                                    d.Values,
                                    slotVals,
                                    info.TargetIndex,
                                    info.SearchTextSlots));
                            }

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
                    await DeadLetterSafelyAsync(sbMessage, "ProcessingError", ex.Message, settled, stoppingToken);
                }
                else
                {
                    _logger.LogError(ex, "Error processing individual search index message in batch. MessageId: {MessageId}, DeliveryCount: {Count}", sbMessage.MessageId, sbMessage.DeliveryCount);
                }
            }
        }

        try
        {
            // Execute bulk actions for the current batch
            IReadOnlyList<SearchIndexFailure> failures = [];
            if (documentsToIndex.Any())
            {
                failures = await searchService.BulkIndexRecordsWithResultsAsync(documentsToIndex, stoppingToken);
            }

            if (recordsToDelete.Any())
            {
                var deleteGroups = recordsToDelete.GroupBy(x => new { x.TenantId, x.TableId });
                foreach (var group in deleteGroups)
                {
                    await searchService.BulkDeleteRecordsAsync(group.Key.TenantId, group.Key.TableId, group.Select(x => x.RecordPublicId).ToList(), stoppingToken);
                }
            }

            // D07: a document Azure AI Search did not accept must not be completed with its message.
            //  - rejected for good (for example 400, an oversized term): dead-letter the message with the reason, so it is kept and visible.
            //  - temporary (409/422/429/503 that outlasted the retries): leave it unsettled so it is redelivered after the lock expires,
            //    and dead-letter it once it has been tried too often.
            foreach (var failure in failures)
            {
                if (!docOwners.TryGetValue(failure.Key, out var owners)) continue;
                foreach (var owner in owners)
                {
                    messagesToComplete.Remove(owner.Message);
                    _logger.LogError(
                        "Search index document {RecordPublicId} (tenant {TenantId}, table {TableId}, message {MessageId}) was not indexed: status {Status}, retryable {Retryable}, delivery {DeliveryCount}, error {Error}",
                        failure.Key, owner.TenantId, owner.TableId, owner.Message.MessageId, failure.Status, failure.Retryable, owner.Message.DeliveryCount, failure.Error);

                    if (!failure.Retryable)
                        await DeadLetterSafelyAsync(owner.Message, "IndexingRejected", $"{failure.Status}: {failure.Error}", settled, stoppingToken);
                    else if (owner.Message.DeliveryCount > 5)
                        await DeadLetterSafelyAsync(owner.Message, "IndexingFailed", $"{failure.Status}: {failure.Error}", settled, stoppingToken);
                }
            }

            // Complete successfully processed messages concurrently to avoid lock expiration
            await Parallel.ForEachAsync(messagesToComplete, new ParallelOptions
            {
                MaxDegreeOfParallelism = 25,
                CancellationToken = stoppingToken
            }, async (sbMessage, ct) =>
            {
                try
                {
                    await _receiver.CompleteMessageAsync(sbMessage, ct);
                }
                catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessageLockLost)
                {
                    _logger.LogWarning("Message lock lost for {MessageId}; message will be redelivered.", sbMessage.MessageId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to complete message {MessageId}.", sbMessage.MessageId);
                }
            });
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
                    await DeadLetterSafelyAsync(sbMessage, "BulkUploadFailed", ex.Message, settled, stoppingToken);
                }
            }
        }
    }

    /// <summary>Dead-letters a message once. A lost lock (or an already-settled message) is logged and left to the broker instead
    /// of throwing out of the batch.</summary>
    private async Task DeadLetterSafelyAsync(ServiceBusReceivedMessage message, string reason, string description, HashSet<string> settled, CancellationToken ct)
    {
        if (!settled.Add(message.MessageId + "|" + message.LockToken)) return;
        try
        {
            await _receiver.DeadLetterMessageAsync(message, reason, Truncate(description, 1000), ct);
        }
        catch (ServiceBusException ex) when (ex.Reason is ServiceBusFailureReason.MessageLockLost or ServiceBusFailureReason.MessageNotFound)
        {
            _logger.LogWarning("Could not dead-letter message {MessageId} ({Reason}): its lock was lost; the broker will redeliver it.", message.MessageId, reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not dead-letter message {MessageId} ({Reason}).", message.MessageId, reason);
        }
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];

    public record TableSlotInfo(
        Dictionary<long, (string Slot, string TypeCode)> SlotMap,
        Dictionary<long, (string Slot, string TypeCode)> StoredSlots,
        HashSet<long> AllFids,
        bool IsWide,
        string? TargetIndex,
        IReadOnlySet<string> SearchTextSlots);

    private readonly ConcurrentDictionary<(long TenantId, long TableId), (TableSlotInfo Info, DateTime LoadedUtc)> _tableSlotCache = new();

    /// <summary>
    /// The table's field-to-column map. Runs in its own scope with the message's tenant set (the field repository reads the
    /// tenant's database; a scope without a tenant throws). The cached copy expires after <see cref="SlotMapTtl"/>, and is
    /// dropped early when a payload carries a field the cached copy has never seen (a field added since). Any failure
    /// propagates, so the message is retried instead of being indexed without its slot values.
    /// </summary>
    private async Task<TableSlotInfo> GetTableSlotInfoAsync(long tenantId, long tableId, IEnumerable<long> payloadFids, CancellationToken ct)
    {
        var key = (tenantId, tableId);
        if (_tableSlotCache.TryGetValue(key, out var cached)
            && DateTime.UtcNow - cached.LoadedUtc < SlotMapTtl
            && payloadFids.All(f => cached.Info.AllFids.Contains(f)))
        {
            return cached.Info;
        }

        using var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IQueryContext>().SetTenantId(tenantId);
        var fieldRepo = scope.ServiceProvider.GetRequiredService<IAppFieldRepository>();

        var fields = await fieldRepo.ListByTableAsync(tableId, ct);
        if (SearchSlotResolver.NeedsSlotAllocation(fields))
        {
            await fieldRepo.EnsureSearchSlotsAsync(tableId, ct);
            fields = await fieldRepo.ListByTableAsync(tableId, ct);
        }

        var info = BuildSlotInfo(tableId, fields);
        _tableSlotCache[key] = (info, DateTime.UtcNow);
        return info;
    }

    public static TableSlotInfo BuildSlotInfo(long tableId, IReadOnlyList<PowerBase.Domain.Entities.AppField> fields)
    {
        var isWide = SearchSlotResolver.IsWideTable(fields);
        var slotMap = SearchSlotResolver.BuildSlotMap(fields);

        var mapped = new Dictionary<long, (string Slot, string TypeCode)>();
        var stored = new Dictionary<long, (string Slot, string TypeCode)>();
        var searchText = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allFids = new HashSet<long>();

        foreach (var f in fields)
        {
            if (!f.Fid.HasValue) continue;
            var fid = (long)f.Fid.Value;
            allFids.Add(fid);

            // Stored slot of any field (even one switched off or of a type not indexed), so a switch-off can clear it.
            if (!isWide && !string.IsNullOrWhiteSpace(f.SearchSlot))
                stored[fid] = (f.SearchSlot!, f.TypeCode);
            if (isWide)
                stored[fid] = ($"f_{fid}", f.TypeCode);

            if (!SearchSlotResolver.IsSearchEligible(f)) continue;
            var slot = slotMap[fid];
            // Outside a wide table's own index, an "f_" name means "no slot": answered from SQL, not indexed here.
            if (!isWide && slot.StartsWith("f_")) continue;

            mapped[fid] = (slot, f.TypeCode);
            if (f.IsSearchable) searchText.Add(slot);
        }

        return new TableSlotInfo(mapped, stored, allFids, isWide, isWide ? SearchSlotResolver.GetTableIndexName(tableId) : null, searchText);
    }
}
