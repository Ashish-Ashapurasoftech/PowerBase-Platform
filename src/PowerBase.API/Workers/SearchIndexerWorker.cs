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
using PowerBase.Infrastructure.Services;

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
    private readonly int _maxDeliveryAttempts;
    private readonly bool _enabled;
    private readonly SearchPipelineState? _state;
    private DateTime _lastMissingQueueLogUtc = DateTime.MinValue;

    /// <summary>How long a table's slot map is trusted. Slots themselves never move once stored, so this only bounds how
    /// long a worker can miss a field that was just added or switched on.</summary>
    internal static readonly TimeSpan SlotMapTtl = TimeSpan.FromSeconds(15);

    /// <summary>How often one process re-requests the move of a table that is waiting to go to its own index.</summary>
    private static readonly TimeSpan MigrationRequestInterval = TimeSpan.FromMinutes(30);
    private static readonly ConcurrentDictionary<(long TenantId, long TableId), DateTime> _migrationRequested = new();

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
        TimeSpan maxWaitTime,
        bool enabled = true)
    {
        _enabled = enabled;
        _serviceProvider = serviceProvider;
        _state = serviceProvider.GetService<SearchPipelineState>();
        _logger = logger;
        _queueName = queueName;
        _maxBatchSize = maxBatchSize;
        _maxWaitTime = maxWaitTime;

        // Same limit as the publisher's retries and the broker's default; must not be higher than the queue's own maximum delivery count.
        _maxDeliveryAttempts = int.TryParse(configuration["AzureServiceBus:MaxDeliveryAttempts"], out var attempts) && attempts > 0
            ? attempts
            : ServiceBusRetry.MaxAttempts;

        _client = ServiceBusRetry.CreateClient(configuration, out _);
        _receiver = _client.CreateReceiver(_queueName, new ServiceBusReceiverOptions
        {
            PrefetchCount = _maxBatchSize
        });
    }

    private int _consecutiveFullBatches = 0;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("{WorkerName} is switched off by configuration; queue {QueueName} will not be read.", GetType().Name, _queueName);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _state?.MarkWorkerLoop(GetType().Name); // alive, even when the queue is empty

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
            catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityNotFound)
            {
                // The queue does not exist (for the priority worker: it has not been created in Service Bus yet). Say so clearly, at most
                // every ten minutes, and look again in a minute instead of failing every few seconds.
                if (DateTime.UtcNow - _lastMissingQueueLogUtc > TimeSpan.FromMinutes(10))
                {
                    _lastMissingQueueLogUtc = DateTime.UtcNow;
                    _logger.LogError("Service Bus queue {QueueName} does not exist, so {WorkerName} cannot read it. Create the queue, or switch this worker off with AzureServiceBus:SearchIndexPriorityQueueEnabled=false. Checking again in a minute.", _queueName, GetType().Name);
                }
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
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

    /// <summary>An upsert waiting for its values to be decided: the message's own payload (null if it carried none).</summary>
    private sealed record PendingUpsert(ServiceBusReceivedMessage SbMessage, SearchIndexMessage Message, IReadOnlyDictionary<long, object?>? Payload);

    private static IReadOnlyDictionary<long, object?>? ParsePayload(SearchIndexMessage message)
    {
        if (message.Payload == null || message.Payload.Count == 0) return null;
        return message.Payload.ToDictionary(
            kvp => long.Parse(kvp.Key),
            kvp =>
            {
                if (kvp.Value is JsonElement el)
                {
                    return el.ValueKind switch
                    {
                        JsonValueKind.String => el.GetString(),
                        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.TryGetDouble(out var d) ? (object)d : el.GetRawText(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Null => null,
                        _ => el.GetRawText()
                    };
                }
                return kvp.Value;
            });
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
        var docOwners = new Dictionary<string, List<DocOwner>>(StringComparer.OrdinalIgnoreCase);
        var recordsToDelete = new List<(long TenantId, long TableId, Guid RecordPublicId)>();
        var messagesToComplete = new List<ServiceBusReceivedMessage>();
        var settled = new HashSet<string>(); // messages already dead-lettered, so nothing settles a message twice
        var upserts = new List<PendingUpsert>();
        // Follow-up work is queued only after this batch's documents are in the index, and only for messages that succeed: a page of a
        // reindex must not be started before the previous page is searchable, and a failed message will produce its own again.
        var continuations = new List<(ServiceBusReceivedMessage Owner, SearchIndexMessage Next)>();
        var finalizations = new List<(ServiceBusReceivedMessage Owner, long TenantId, long TableId)>();
        // Changes this batch settles, to be confirmed to the outbox once the index really holds them.
        var pendingAcks = new List<(long TenantId, long TableId, SearchOutboxAck Ack, ServiceBusReceivedMessage Owner)>();

        void Track(ServiceBusReceivedMessage message, SearchIndexDocument doc)
        {
            var key = doc.PublicId.ToString();
            if (!docOwners.TryGetValue(key, out var owners)) docOwners[key] = owners = new List<DocOwner>();
            owners.Add(new DocOwner(message, doc.TenantId, doc.TableId));
        }

        void AddDocument(ServiceBusReceivedMessage message, SearchIndexDocument doc)
        {
            documentsToIndex.Add(doc);
            Track(message, doc);
        }

        async Task FailAsync(IEnumerable<ServiceBusReceivedMessage> failed, Exception ex)
        {
            foreach (var m in failed)
            {
                messagesToComplete.Remove(m);
                if (m.DeliveryCount >= _maxDeliveryAttempts)
                {
                    _logger.LogCritical(ex, "Search index message {MessageId} failed processing {Count} times. Moving to DLQ.", m.MessageId, m.DeliveryCount);
                    await DeadLetterSafelyAsync(m, "ProcessingError", ex.Message, settled, stoppingToken);
                }
                else
                {
                    _logger.LogError(ex, "Error processing search index message {MessageId}, DeliveryCount: {Count}", m.MessageId, m.DeliveryCount);
                }
            }
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
                    // Decided below, per table, once it is known whether this payload can be trusted.
                    upserts.Add(new PendingUpsert(sbMessage, message, ParsePayload(message)));
                }
                else if (message.Action == IndexAction.Delete)
                {
                    recordsToDelete.Add((message.TenantId, message.TableId, message.RecordPublicId));
                    pendingAcks.Add((message.TenantId, message.TableId, new SearchOutboxAck(message.RecordPublicId, message.Version, true, true), sbMessage));
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
                            var info = await GetTableSlotInfoAsync(message.TenantId, message.TableId, [fid], false, stoppingToken);
                            // A deleted field is no longer in the table, so the message names the columns it used.
                            var explicitClear = isNullify && message.Slot != null;
                            foreach (var d in docs)
                                foreach (var doc in (explicitClear
                                                 ? SearchIndexDocumentBuilder.BuildExplicitClear(info, d, fid, message.Slot!, message.EndSlot)
                                                 : SearchIndexDocumentBuilder.BuildFieldChange(info, d.TenantId, d.AppId, d.TableId, d.PublicId, d.Values, fid, isNullify))
                                             .Where(x => x.SlotValues is { Count: > 0 }))
                                    AddDocument(sbMessage, doc);

                            // The next page, queued once this one is indexed
                            if (docs.Count == 500)
                            {
                                continuations.Add((sbMessage, new SearchIndexMessage
                                {
                                    Action = message.Action,
                                    TenantId = message.TenantId,
                                    AppId = message.AppId,
                                    TableId = message.TableId,
                                    FieldId = message.FieldId,
                                    Page = message.Page + 1,
                                    Slot = message.Slot,
                                    EndSlot = message.EndSlot
                                }));
                            }
                        }
                    }
                }
                else if (message.Action == IndexAction.ReindexTable)
                {
                    // Copies one page of the table. Also the copy step of moving a table to its own index (the table is "migrating":
                    // every document is built for both indexes), and a plain recovery reindex of a table that is not moving.
                    using var scope = _serviceProvider.CreateScope();
                    scope.ServiceProvider.GetRequiredService<IQueryContext>().SetTenantId(message.TenantId);
                    var recordRepo = scope.ServiceProvider.GetRequiredService<IRecordRepository>();

                    var info = await GetTableSlotInfoAsync(message.TenantId, message.TableId, [], true, stoppingToken);
                    var page = Math.Max(1, message.Page);
                    var docs = await recordRepo.GetTableIndexBatchAsync(message.TenantId, message.TableId, page, SearchWideMigration.PageSize, stoppingToken);
                    foreach (var d in docs)
                        foreach (var doc in SearchIndexDocumentBuilder.Build(info, d.TenantId, d.AppId, d.TableId, d.PublicId, d.Values, null))
                            AddDocument(sbMessage, doc);

                    if (docs.Count >= SearchWideMigration.PageSize)
                        continuations.Add((sbMessage, SearchWideMigration.Message(message.TenantId, message.AppId, message.TableId, page + 1)));
                    else
                        finalizations.Add((sbMessage, message.TenantId, message.TableId));
                }

                messagesToComplete.Add(sbMessage);
            }
            catch (Exception ex)
            {
                await FailAsync([sbMessage], ex);
            }
        }

        // ---- upserts: decide, per table, where each record's values come from.
        foreach (var group in upserts.GroupBy(u => (u.Message.TenantId, u.Message.TableId)))
        {
            var groupMessages = group.Select(u => u.SbMessage).ToList();
            try
            {
                var (tenantId, tableId) = group.Key;
                var info = await GetTableSlotInfoAsync(tenantId, tableId, group.Where(u => u.Payload != null).SelectMany(u => u.Payload!.Keys), false, stoppingToken);

                // What the index already holds, for first deliveries only: a redelivery is read from the database regardless.
                IReadOnlyDictionary<Guid, long> indexed = new Dictionary<Guid, long>();
                if (group.Any(u => u.Payload != null && u.SbMessage.DeliveryCount <= 1))
                    indexed = await searchService.GetSourceVersionsAsync(tenantId, tableId, info.TargetIndex,
                        group.Select(u => u.Message.RecordPublicId).Distinct().ToList(), stoppingToken);

                // A payload is a snapshot taken at commit time. It is used only when nothing suggests it is out of date; otherwise the
                // record's current values are read from the database (the source of truth), so an old or replayed message can never
                // write old values over newer ones.
                var authoritative = new HashSet<Guid>();
                foreach (var u in group)
                {
                    long? indexedVersion = indexed.TryGetValue(u.Message.RecordPublicId, out var iv) ? iv : null;
                    if (SearchIndexDocumentBuilder.NeedsAuthoritativeRead(u.Payload != null, u.SbMessage.DeliveryCount, u.Message.Version, indexedVersion))
                        authoritative.Add(u.Message.RecordPublicId);
                }

                IReadOnlyDictionary<Guid, IReadOnlyDictionary<long, object?>> current = new Dictionary<Guid, IReadOnlyDictionary<long, object?>>();
                if (authoritative.Count > 0)
                {
                    using var scope = _serviceProvider.CreateScope();
                    scope.ServiceProvider.GetRequiredService<IQueryContext>().SetTenantId(tenantId);
                    var recordRepo = scope.ServiceProvider.GetRequiredService<IRecordRepository>();
                    current = await recordRepo.GetSearchableFieldsBatchAsync(tableId, authoritative.ToList(), stoppingToken);
                }

                var built = new List<SearchIndexDocument>();
                foreach (var u in group.OrderBy(u => u.Message.Version))
                {
                    var id = u.Message.RecordPublicId;
                    IReadOnlyDictionary<long, object?>? data;
                    if (authoritative.Contains(id))
                    {
                        if (!current.TryGetValue(id, out data))
                        {
                            // Gone from the database (deleted): gone from the index too.
                            recordsToDelete.Add((tenantId, tableId, id));
                            pendingAcks.Add((tenantId, tableId, new SearchOutboxAck(id, u.Message.Version, true, true), u.SbMessage));
                            continue;
                        }
                    }
                    else
                    {
                        data = u.Payload!;
                    }
                    // The outbox row is confirmed when the record is in the index as of this message: everything up to it when the values were
                    // read from the database, only this very change when they came from a payload (which holds just the fields it touched).
                    pendingAcks.Add((tenantId, tableId, new SearchOutboxAck(id, u.Message.Version, authoritative.Contains(id), false), u.SbMessage));
                    if (data.Count == 0) continue; // nothing of this record is indexed

                    var version = Math.Max(u.Message.Version, indexed.TryGetValue(id, out var known) ? known : 0);
                    foreach (var doc in SearchIndexDocumentBuilder.Build(info, tenantId, u.Message.AppId, tableId, id, data, version))
                    {
                        built.Add(doc);
                        Track(u.SbMessage, doc);
                    }
                }

                // Oldest first, later values win, one document per record and index.
                documentsToIndex.AddRange(SearchIndexDocumentBuilder.MergeByRecord(built));
            }
            catch (Exception ex)
            {
                await FailAsync(groupMessages, ex);
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
                    await searchService.BulkDeleteRecordsAsync(group.Key.TenantId, group.Key.TableId, group.Select(x => x.RecordPublicId).Distinct().ToList(), stoppingToken);
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
                    else if (owner.Message.DeliveryCount >= _maxDeliveryAttempts)
                        await DeadLetterSafelyAsync(owner.Message, "IndexingFailed", $"{failure.Status}: {failure.Error}", settled, stoppingToken);
                }
            }

            // Confirm to the outbox what the index now holds: never a document that failed, nor a message that is going to be retried.
            var failedKeys = failures.Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var settledGroup in pendingAcks
                         .Where(a => messagesToComplete.Contains(a.Owner) && !failedKeys.Contains(a.Ack.RecordPublicId.ToString()))
                         .GroupBy(a => (a.TenantId, a.TableId)))
            {
                try
                {
                    using var ackScope = _serviceProvider.CreateScope();
                    ackScope.ServiceProvider.GetRequiredService<IQueryContext>().SetTenantId(settledGroup.Key.TenantId);
                    await ackScope.ServiceProvider.GetRequiredService<ISearchOutbox>()
                        .AcknowledgeAsync(settledGroup.Key.TableId, settledGroup.Select(a => a.Ack).ToList(), stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Not fatal: an unconfirmed row is simply re-published by the relay and confirmed then (indexing is idempotent).
                    _logger.LogWarning(ex, "Could not confirm {Count} indexed change(s) of table {TableId} to the outbox; the relay will re-publish them.",
                        settledGroup.Count(), settledGroup.Key.TableId);
                }
            }
            _state?.MarkBatch(GetType().Name, Math.Max(0, documentsToIndex.Count - failedKeys.Count), failedKeys.Count);

            // Follow-up work for the messages that made it: the next page, and the end of a table move.
            foreach (var (owner, next) in continuations)
            {
                if (!messagesToComplete.Contains(owner)) continue;
                using var scope = _serviceProvider.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IMessagePublisher>().PublishAsync(next, stoppingToken);
            }
            foreach (var (owner, tenantId, tableId) in finalizations)
            {
                if (!messagesToComplete.Contains(owner)) continue;
                using var scope = _serviceProvider.CreateScope();
                scope.ServiceProvider.GetRequiredService<IQueryContext>().SetTenantId(tenantId);
                var fieldRepo = scope.ServiceProvider.GetRequiredService<IAppFieldRepository>();
                if (await SearchWideMigration.FinalizeAsync(fieldRepo, searchService, tenantId, tableId, stoppingToken))
                {
                    _tableSlotCache.TryRemove((tenantId, tableId), out _);
                    _logger.LogInformation("Table {TableId} (tenant {TenantId}) was moved to its own search index {Index}.", tableId, tenantId, SearchSlotResolver.GetTableIndexName(tableId));
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
                if (sbMessage.DeliveryCount >= _maxDeliveryAttempts && !messagesToComplete.Contains(sbMessage))
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
            _state?.MarkDeadLettered();
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

    private readonly ConcurrentDictionary<(long TenantId, long TableId), (SearchTableSlotInfo Info, DateTime LoadedUtc)> _tableSlotCache = new();

    /// <summary>
    /// The table's field-to-column map. Runs in its own scope with the message's tenant set (the field repository reads the
    /// tenant's database; a scope without a tenant throws). The cached copy expires after <see cref="SlotMapTtl"/>, and is
    /// dropped early when a payload carries a field the cached copy has never seen (a field added since). Any failure
    /// propagates, so the message is retried instead of being indexed without its slot values.
    /// </summary>
    private async Task<SearchTableSlotInfo> GetTableSlotInfoAsync(long tenantId, long tableId, IEnumerable<long> payloadFids, bool forceRefresh, CancellationToken ct)
    {
        var key = (tenantId, tableId);
        if (!forceRefresh
            && _tableSlotCache.TryGetValue(key, out var cached)
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

        var info = SearchTableSlotInfo.From(tableId, fields);
        _tableSlotCache[key] = (info, DateTime.UtcNow);

        // Safety net: a table over the shared index's limit that nobody has started moving (a field created without a toggle). Dual-write
        // already keeps the shared index complete meanwhile; this starts the copy. Not from a reindex message itself.
        if (info.MigratingToWide && !forceRefresh
            && (!_migrationRequested.TryGetValue(key, out var last) || DateTime.UtcNow - last > MigrationRequestInterval))
        {
            _migrationRequested[key] = DateTime.UtcNow;
            if (!await scope.ServiceProvider.GetRequiredService<IAzureSearchService>().CanUseWideIndexAsync(tableId, ct)) return info;
            await scope.ServiceProvider.GetRequiredService<IMessagePublisher>().PublishAsync(SearchWideMigration.Message(tenantId, 0, tableId, 1), ct);
            _logger.LogInformation("Table {TableId} (tenant {TenantId}) has outgrown the shared search index; moving it to its own index.", tableId, tenantId);
        }

        return info;
    }
}
