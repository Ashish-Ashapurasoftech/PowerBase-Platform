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

    /// <summary>How long a table's slot map is trusted. Slots themselves never move once stored, and a field that was just added or
    /// switched on reloads the map as soon as a message or a database read names it, so this only bounds other metadata changes.
    /// Field jobs and reindexes always read the map fresh.</summary>
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

    /// <summary>An upsert waiting for its record to be read from the database.</summary>
    private sealed record PendingUpsert(ServiceBusReceivedMessage SbMessage, SearchIndexMessage Message);

    /// <summary>Whether a record changed between two version reads: its version moved, or it appeared or vanished.</summary>
    public static bool RowChanged(IReadOnlyDictionary<Guid, long> before, IReadOnlyDictionary<Guid, long> after, Guid id)
    {
        var hadBefore = before.TryGetValue(id, out var b);
        var hasAfter = after.TryGetValue(id, out var a);
        return hadBefore != hasAfter || (hasAfter && a != b);
    }

    /// <summary>One page of a paging job, by record id. A message queued before paging by id existed carries only a page number: it is
    /// read by position once, and its follow-up continues by id.</summary>
    private static Task<(IReadOnlyList<SearchIndexDocument> Documents, long LastId)> ReadPageAsync(
        IRecordRepository recordRepo, SearchIndexMessage message, CancellationToken ct) =>
        message.AfterId is long afterId
            ? recordRepo.GetTableIndexPageAsync(message.TenantId, message.TableId, afterId, SearchWideMigration.PageSize, ct)
            : recordRepo.GetTableIndexPageAsync(message.TenantId, message.TableId, 0, SearchWideMigration.PageSize, ct,
                skip: (Math.Max(1, message.Page) - 1) * SearchWideMigration.PageSize);

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
        var slotReleases = new List<(ServiceBusReceivedMessage Owner, long TenantId, long TableId, long Fid, string Slot)>();
        var versionChecks = new List<(long TenantId, long TableId, long AppId, IReadOnlyDictionary<Guid, long> Before, List<PendingUpsert> Upserts)>();
        // Pages of recorded field jobs: the job moves from FromAfterId to the next page's start (Next), or ends (Next null).
        var jobSteps = new List<(ServiceBusReceivedMessage Owner, long TenantId, long JobId, long FromAfterId, SearchIndexMessage? Next)>();
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
                    // Read below, per table, in one query.
                    upserts.Add(new PendingUpsert(sbMessage, message));
                }
                else if (message.Action == IndexAction.Delete)
                {
                    recordsToDelete.Add((message.TenantId, message.TableId, message.RecordPublicId));
                    pendingAcks.Add((message.TenantId, message.TableId, new SearchOutboxAck(message.RecordPublicId, message.Version, true, true), sbMessage));
                }
                else if (message.Action == IndexAction.BackfillField || message.Action == IndexAction.NullifyField)
                {
                    // One page of a field change: every record of the page is rewritten in full (all indexed values, the free-text
                    // field rebuilt), with the column(s) the field no longer uses emptied. Rewriting the whole record is what removes
                    // a switched-off or deleted field's text from quick search, and what writes a reused column for every record.
                    using var scope = _serviceProvider.CreateScope();
                    var queryContext = scope.ServiceProvider.GetRequiredService<IQueryContext>();
                    queryContext.SetTenantId(message.TenantId);
                    var recordRepo = scope.ServiceProvider.GetRequiredService<IRecordRepository>();

                    var isNullify = message.Action == IndexAction.NullifyField;
                    if (message.FieldId.HasValue)
                    {
                        var fid = message.FieldId.Value;
                        // Never a cached copy: a field that was switched on a moment ago must find the column it was just given.
                        var info = await GetTableSlotInfoAsync(message.TenantId, message.TableId, [], true, stoppingToken);
                        // A deleted field, or the old column of a converted one, is named by the message: the table no longer tells it.
                        var clearSlot = message.Slot != null ? new SlotRef(message.Slot, string.Empty, message.EndSlot) : null;
                        var plan = new FieldSlotSync.Plan(info, (int)fid, isNullify, clearSlot);
                        SearchIndexMessage? next = null;
                        if (plan.HasTarget)
                        {
                            var (docs, lastId) = await ReadPageAsync(recordRepo, message, stoppingToken);
                            foreach (var doc in FieldSlotSync.ToDocuments(docs, plan))
                                AddDocument(sbMessage, doc);

                            // The next page, queued once this one is indexed
                            if (docs.Count >= SearchWideMigration.PageSize)
                                next = FieldSlotSync.Message(message.TenantId, message.AppId, message.TableId, fid, isNullify, clearSlot, lastId, message.JobId);
                            // The last page of emptying a column: once it is in the index no document holds the old values any more,
                            // so a deleted field's column can go back to the pool (a live field's old column already has).
                            else if (isNullify && clearSlot != null)
                                slotReleases.Add((sbMessage, message.TenantId, message.TableId, fid, clearSlot.Slot));
                        }

                        // A recorded job moves forward (or ends) only once this page is in the index; with no column left to write
                        // it simply ends.
                        if (message.JobId is long jobId)
                            jobSteps.Add((sbMessage, message.TenantId, jobId, message.AfterId ?? 0, next));
                        else if (next != null)
                            continuations.Add((sbMessage, next));
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
                    var (docs, lastId) = await ReadPageAsync(recordRepo, message, stoppingToken);
                    foreach (var d in docs)
                        foreach (var doc in SearchIndexDocumentBuilder.Build(info, d.TenantId, d.AppId, d.TableId, d.PublicId, d.Values, null, complete: true))
                            AddDocument(sbMessage, doc);

                    if (docs.Count >= SearchWideMigration.PageSize)
                        continuations.Add((sbMessage, SearchWideMigration.Message(message.TenantId, message.AppId, message.TableId, Math.Max(1, message.Page) + 1, lastId)));
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

        // ---- upserts: every record is read from the database, the source of truth. A message carries ids only and is sent after its
        // write commits, so the read always sees the change (or a newer one: an old or replayed message can never write old values). The
        // read holds every indexed field, so the free-text field (quick search) is rebuilt from ALL of the record's searchable text, and
        // it brings the system fields (Date Modified, Record ID#...), which the database sets.
        foreach (var group in upserts.GroupBy(u => (u.Message.TenantId, u.Message.TableId)))
        {
            var groupMessages = group.Select(u => u.SbMessage).ToList();
            try
            {
                var (tenantId, tableId) = group.Key;
                var info = await GetTableSlotInfoAsync(tenantId, tableId, [], false, stoppingToken);

                IReadOnlyDictionary<Guid, IReadOnlyDictionary<long, object?>> current;
                var recordIds = group.Select(u => u.Message.RecordPublicId).Distinct().ToList();
                using (var scope = _serviceProvider.CreateScope())
                {
                    scope.ServiceProvider.GetRequiredService<IQueryContext>().SetTenantId(tenantId);
                    var recordRepo = scope.ServiceProvider.GetRequiredService<IRecordRepository>();
                    // Each record's version BEFORE its values are read: if it differs once the documents are written, the record changed
                    // meanwhile, and another worker may have written the newer state before this one wrote the older (see below).
                    versionChecks.Add((tenantId, tableId, group.First().Message.AppId,
                        await recordRepo.GetRowVersionsAsync(tableId, recordIds, stoppingToken), group.ToList()));
                    current = await recordRepo.GetSearchableFieldsBatchAsync(tableId, recordIds, stoppingToken);
                }

                // The read lists the fields that are indexed now. One the cached map does not know as eligible was switched on since it
                // was loaded: reload, or its value would be dropped (and the change confirmed to the outbox without it).
                if (current.Values.Any(v => v.Keys.Any(fid => !info.EligibleFids.Contains(fid))))
                    info = await GetTableSlotInfoAsync(tenantId, tableId, [], true, stoppingToken);

                var built = new List<SearchIndexDocument>();
                foreach (var u in group.OrderBy(u => u.Message.Version))
                {
                    var id = u.Message.RecordPublicId;
                    if (!current.TryGetValue(id, out var row))
                    {
                        // Gone from the database (deleted): gone from the index too.
                        recordsToDelete.Add((tenantId, tableId, id));
                        pendingAcks.Add((tenantId, tableId, new SearchOutboxAck(id, u.Message.Version, true, true), u.SbMessage));
                        continue;
                    }
                    // The record is in the index as of the read, which came after this change committed: every outbox row of the record
                    // up to this message is confirmed.
                    pendingAcks.Add((tenantId, tableId, new SearchOutboxAck(id, u.Message.Version, true, false), u.SbMessage));
                    if (row.Count == 0) continue; // nothing of this record is indexed

                    foreach (var doc in SearchIndexDocumentBuilder.Build(info, tenantId, u.Message.AppId, tableId, id, row, u.Message.Version, complete: true))
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

            // A record that changed between its read and now: another worker (or the priority queue) may have written its newer state
            // first and this batch the older one over it. It is queued once more (outbox row first, so the relay covers a failed send),
            // and that read is the latest state. A record that did not change is in the index as it is now, whatever the order of writes.
            foreach (var check in versionChecks)
            {
                var written = check.Upserts.Where(u => messagesToComplete.Contains(u.SbMessage)).Select(u => u.Message.RecordPublicId).Distinct().ToList();
                if (written.Count == 0) continue;
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    scope.ServiceProvider.GetRequiredService<IQueryContext>().SetTenantId(check.TenantId);
                    var after = await scope.ServiceProvider.GetRequiredService<IRecordRepository>().GetRowVersionsAsync(check.TableId, written, stoppingToken);
                    var changed = written.Where(id => RowChanged(check.Before, after, id)).ToList();
                    if (changed.Count == 0) continue;

                    var again = changed.Select(id => new SearchIndexMessage
                    {
                        Action = IndexAction.Upsert, TenantId = check.TenantId, AppId = check.AppId, TableId = check.TableId, RecordPublicId = id
                    }).ToList();
                    await scope.ServiceProvider.GetRequiredService<ISearchOutbox>().AddAsync(again, null, stoppingToken);
                    await scope.ServiceProvider.GetRequiredService<IMessagePublisher>().PublishBatchAsync(again, stoppingToken);
                    _logger.LogInformation("{Count} record(s) of table {TableId} changed while being indexed; queued again so the index ends on their latest state.",
                        changed.Count, check.TableId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Not fatal: the change that moved the version has its own message, and its outbox row stays until it is indexed.
                    _logger.LogWarning(ex, "Could not re-check the versions of {Count} indexed record(s) of table {TableId}.", written.Count, check.TableId);
                }
            }
            _state?.MarkBatch(GetType().Name, Math.Max(0, documentsToIndex.Count - failedKeys.Count), failedKeys.Count);

            // Recorded field jobs: compare-and-set from the page's start, so a job the relay re-sent and its original never both carry on
            // (the run that loses stops here). A failure throws out to the batch retry; if the job moved but its next page was not sent,
            // the relay re-sends it from there once its progress has stopped.
            foreach (var (owner, tenantId, jobId, fromAfterId, next) in jobSteps)
            {
                if (!messagesToComplete.Contains(owner)) continue;
                using var scope = _serviceProvider.CreateScope();
                scope.ServiceProvider.GetRequiredService<IQueryContext>().SetTenantId(tenantId);
                var jobs = scope.ServiceProvider.GetRequiredService<ISearchFieldJobs>();
                if (next == null)
                    await jobs.CompleteAsync(jobId, fromAfterId, stoppingToken);
                else if (await jobs.AdvanceAsync(jobId, fromAfterId, next.AfterId!.Value, stoppingToken))
                    await scope.ServiceProvider.GetRequiredService<IMessagePublisher>().PublishAsync(next, stoppingToken);
            }

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
            foreach (var (owner, tenantId, tableId, fid, slot) in slotReleases)
            {
                if (!messagesToComplete.Contains(owner)) continue;
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    scope.ServiceProvider.GetRequiredService<IQueryContext>().SetTenantId(tenantId);
                    if (await scope.ServiceProvider.GetRequiredService<IAppFieldRepository>().ReleaseDeletedFieldSlotAsync(tableId, fid, slot, stoppingToken) > 0)
                    {
                        _tableSlotCache.TryRemove((tenantId, tableId), out _);
                        _logger.LogInformation("Search column {Slot} of deleted field {Fid} (table {TableId}, tenant {TenantId}) is empty in every document and was freed.", slot, fid, tableId, tenantId);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Not fatal: the column simply stays reserved, as it was before.
                    _logger.LogWarning(ex, "Could not free search column {Slot} of deleted field {Fid} in table {TableId}.", slot, fid, tableId);
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
